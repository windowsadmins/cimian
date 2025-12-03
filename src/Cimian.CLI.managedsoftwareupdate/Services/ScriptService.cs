using System.Management.Automation;
using System.Text;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Service for executing PowerShell scripts
/// Migrated from Go pkg/scripts
/// </summary>
public class ScriptService
{
    /// <summary>
    /// Executes a PowerShell script from string content
    /// </summary>
    public async Task<(bool Success, string Output)> ExecuteScriptAsync(
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
    /// Executes a PowerShell script from a file
    /// </summary>
    public async Task<(bool Success, string Output)> ExecuteScriptFileAsync(
        string scriptPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(scriptPath))
        {
            return (false, $"Script file not found: {scriptPath}");
        }

        var scriptContent = await File.ReadAllTextAsync(scriptPath, cancellationToken);
        return await ExecuteScriptAsync(scriptContent, cancellationToken);
    }

    /// <summary>
    /// Runs the preflight script if it exists
    /// </summary>
    public async Task<(bool Success, string Output)> RunPreflightAsync(
        CancellationToken cancellationToken = default)
    {
        var preflightPath = @"C:\ProgramData\ManagedInstalls\sbin\preflight.ps1";
        
        if (!File.Exists(preflightPath))
        {
            return (true, "No preflight script found");
        }

        Console.WriteLine("[INFO] Executing preflight script...");
        return await ExecuteScriptFileAsync(preflightPath, cancellationToken);
    }

    /// <summary>
    /// Runs the postflight script if it exists
    /// </summary>
    public async Task<(bool Success, string Output)> RunPostflightAsync(
        CancellationToken cancellationToken = default)
    {
        var postflightPath = @"C:\ProgramData\ManagedInstalls\sbin\postflight.ps1";
        
        if (!File.Exists(postflightPath))
        {
            return (true, "No postflight script found");
        }

        Console.WriteLine("[INFO] Executing postflight script...");
        return await ExecuteScriptFileAsync(postflightPath, cancellationToken);
    }
}
