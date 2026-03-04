using System.Diagnostics;
using System.Management.Automation;
using System.Text;
using Cimian.Core.Services;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Service for executing PowerShell scripts
/// Migrated from Go pkg/scripts
/// </summary>
public class ScriptService
{
    /// <summary>
    /// Executes a PowerShell script from string content using in-process SDK
    /// Note: This method has limitations with exit codes - use ExecuteScriptWithExitCodeAsync for scripts that rely on exit codes
    /// </summary>
    public async Task<(bool Success, string Output)> ExecuteScriptAsync(
        string scriptContent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scriptContent))
        {
            return (true, "No script content to execute");
        }

        // For scripts that use exit codes (like installcheck scripts), use external process
        // This ensures proper exit code handling for Go parity
        return await ExecuteScriptWithExitCodeAsync(scriptContent, cancellationToken);
    }

    /// <summary>
    /// Executes a PowerShell script from string content using external process
    /// This properly captures exit codes (Go parity behavior)
    /// </summary>
    public async Task<(bool Success, string Output)> ExecuteScriptWithExitCodeAsync(
        string scriptContent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scriptContent))
        {
            return (true, "No script content to execute");
        }

        // Find PowerShell executable (prefer pwsh over powershell)
        var psExe = FindPowerShellExecutable();
        if (string.IsNullOrEmpty(psExe))
        {
            return (false, "Neither pwsh.exe nor powershell.exe was found");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = psExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                CreateNoWindow = true,
            };

            // Use -Command to execute inline script, properly escaping
            // -Command interprets the rest as a PowerShell command
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(scriptContent);

            using var process = new Process { StartInfo = startInfo };
            var output = new StringBuilder();
            var errors = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    output.AppendLine(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    errors.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);
            // WaitForExitAsync only waits for the process to exit; the async stream
            // callbacks may still be draining. Calling the synchronous overload ensures
            // stdout/stderr buffers are fully flushed before we read them.
            process.WaitForExit();

            var exitCode = process.ExitCode;
            var stdoutText = output.ToString().TrimEnd();
            var stderrText = errors.ToString().TrimEnd();

            // Build a labelled combined output so callers can see both streams
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(stdoutText))
                parts.Add(stdoutText);
            if (!string.IsNullOrEmpty(stderrText))
                parts.Add($"STDERR: {stderrText}");
            var combinedOutput = parts.Count > 0
                ? string.Join(Environment.NewLine, parts)
                : $"(no output, exit code {exitCode})";

            // Exit code 0 = success, non-zero = failure
            return (exitCode == 0, combinedOutput);
        }
        catch (Exception ex)
        {
            return (false, $"Script execution failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Executes a PowerShell script from string content using in-process SDK
    /// Note: This method does NOT properly handle exit codes - only use for simple scripts without exit statements
    /// </summary>
    public async Task<(bool Success, string Output)> ExecuteScriptInProcessAsync(
        string scriptContent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scriptContent))
        {
            return (true, "No script content to execute");
        }

        try
        {
            using var ps = PowerShell.Create();
            
            // Set execution policy for this runspace
            ps.AddCommand("Set-ExecutionPolicy")
              .AddParameter("ExecutionPolicy", "Bypass")
              .AddParameter("Scope", "Process")
              .AddParameter("Force");
            await ps.InvokeAsync();
            ps.Commands.Clear();

            // Execute the actual script
            ps.AddScript(scriptContent);
            
            var output = new StringBuilder();
            var results = await ps.InvokeAsync();

            foreach (var result in results)
            {
                output.AppendLine(result?.ToString() ?? "");
            }

            // Check for errors
            if (ps.HadErrors)
            {
                foreach (var error in ps.Streams.Error)
                {
                    output.AppendLine($"ERROR: {error}");
                }
                return (false, output.ToString());
            }

            return (true, output.ToString());
        }
        catch (Exception ex)
        {
            return (false, $"Script execution failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Executes a PowerShell script from a file using external pwsh/powershell process
    /// This preserves script-level variables like $PSCommandPath (matching Go behavior)
    /// </summary>
    public async Task<(bool Success, string Output)> ExecuteScriptFileAsync(
        string scriptPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(scriptPath))
        {
            return (false, $"Script file not found: {scriptPath}");
        }

        // Find PowerShell executable (prefer pwsh over powershell)
        var psExe = FindPowerShellExecutable();
        if (string.IsNullOrEmpty(psExe))
        {
            return (false, "Neither pwsh.exe nor powershell.exe was found");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = psExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? ""
            };

            // Build arguments properly to handle paths with spaces
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);

            // Set TERM so ANSI colors are preserved (matching Go behavior)
            startInfo.Environment["TERM"] = "xterm-256color";

            using var process = new Process { StartInfo = startInfo };
            var output = new StringBuilder();
            var errors = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    output.AppendLine(e.Data);
                    // Stream output to console in real-time
                    Console.WriteLine(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    errors.AppendLine(e.Data);
                    Console.Error.WriteLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);

            var combinedOutput = output.ToString();
            if (errors.Length > 0)
            {
                combinedOutput += Environment.NewLine + errors.ToString();
            }

            return (process.ExitCode == 0, combinedOutput);
        }
        catch (Exception ex)
        {
            return (false, $"Script execution failed: {ex.Message}");
        }
    }

    private static string? FindPowerShellExecutable()
    {
        // Use Windows PowerShell 5.1 directly to avoid the preflight script's
        // re-invocation logic which has path quoting issues.
        // The preflight script requires Windows PowerShell 5.1 for PackageManagement modules.
        var winPsPath = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
        if (File.Exists(winPsPath))
        {
            return winPsPath;
        }

        // Fall back to PowerShell Core if Windows PowerShell not found
        var pwshPaths = new[]
        {
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            @"C:\Program Files\PowerShell\pwsh.exe"
        };

        foreach (var path in pwshPaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        // Try to find in PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';'))
        {
            var pwshPath = Path.Combine(dir, "pwsh.exe");
            if (File.Exists(pwshPath))
            {
                return pwshPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Runs the preflight script if it exists
    /// </summary>
    public async Task<(bool Success, string Output)> RunPreflightAsync(
        CancellationToken cancellationToken = default)
    {
        // Check multiple possible locations (matching Go behavior)
        var possiblePaths = new[]
        {
            @"C:\Program Files\Cimian\preflight.ps1",
            @"C:\ProgramData\ManagedInstalls\sbin\preflight.ps1"
        };

        string? preflightPath = null;
        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
            {
                preflightPath = path;
                break;
            }
        }
        
        if (preflightPath == null)
        {
            return (true, "No preflight script found");
        }

        ConsoleLogger.Info($"Executing preflight script: {preflightPath}");
        return await ExecuteScriptFileAsync(preflightPath, cancellationToken);
    }

    /// <summary>
    /// Runs the postflight script if it exists
    /// </summary>
    public async Task<(bool Success, string Output)> RunPostflightAsync(
        CancellationToken cancellationToken = default)
    {
        // Check multiple possible locations (matching Go behavior)
        var possiblePaths = new[]
        {
            @"C:\Program Files\Cimian\postflight.ps1",
            @"C:\ProgramData\ManagedInstalls\sbin\postflight.ps1"
        };

        string? postflightPath = null;
        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
            {
                postflightPath = path;
                break;
            }
        }
        
        if (postflightPath == null)
        {
            return (true, "No postflight script found");
        }

        ConsoleLogger.Info($"Executing postflight script: {postflightPath}");
        return await ExecuteScriptFileAsync(postflightPath, cancellationToken);
    }
}
