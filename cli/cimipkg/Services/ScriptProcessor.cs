using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Cimian.CLI.Cimipkg.Services;

/// <summary>
/// Processes scripts by replacing placeholders with environment variables
/// and injecting variable mapping headers for PowerShell scripts.
/// </summary>
public class ScriptProcessor
{
    private readonly ILogger<ScriptProcessor> _logger;

    // Placeholder pattern matches ${VAR_NAME} or $VAR_NAME
    private static readonly Regex PlaceholderPattern = new(
        @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    // YAML-safe placeholder pattern (only ${VAR_NAME} to avoid conflicts with YAML syntax)
    private static readonly Regex YamlPlaceholderPattern = new(
        @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}",
        RegexOptions.Compiled);

    /// <summary>
    /// PowerShell script extensions.
    /// </summary>
    private static readonly HashSet<string> PowerShellExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".psm1", ".psd1"
    };

    /// <summary>
    /// All script extensions that support placeholder replacement.
    /// </summary>
    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".psm1", ".psd1", ".sh", ".cmd", ".bat"
    };

    /// <summary>
    /// Header injected into PowerShell scripts for .pkg packages.
    /// Maps environment variables to local variables for consistency with Chocolatey wrapper scripts.
    /// </summary>
    private const string PowerShellVariableMappingHeader = @"# cimipkg: Auto-mapped variables for consistency
if ($env:payloadRoot -and -not $payloadRoot) { $payloadRoot = $env:payloadRoot }
if ($env:payloadDir -and -not $payloadDir) { $payloadDir = $env:payloadDir }
if ($env:installLocation -and -not $installLocation) { $installLocation = $env:installLocation }

";

    public ScriptProcessor(ILogger<ScriptProcessor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Replaces placeholders in content with environment variable values.
    /// </summary>
    /// <param name="content">The content to process.</param>
    /// <param name="envVars">Dictionary of environment variables to inject.</param>
    /// <returns>Content with placeholders replaced.</returns>
    public string ReplacePlaceholders(string content, IDictionary<string, string> envVars)
    {
        if (string.IsNullOrEmpty(content) || envVars == null || envVars.Count == 0)
        {
            return content;
        }

        return PlaceholderPattern.Replace(content, match =>
        {
            // Get the variable name from either ${VAR} or $VAR pattern
            var varName = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;

            if (envVars.TryGetValue(varName, out var value))
            {
                _logger.LogDebug("Replaced placeholder ${{{VarName}}} with value", varName);
                return value;
            }

            // If no replacement found, keep the original placeholder
            return match.Value;
        });
    }

    /// <summary>
    /// Replaces placeholders in YAML content (only ${VAR} pattern to avoid conflicts).
    /// </summary>
    /// <param name="content">The YAML content to process.</param>
    /// <param name="envVars">Dictionary of environment variables to inject.</param>
    /// <returns>Content with placeholders replaced.</returns>
    public string ReplacePlaceholdersYaml(string content, IDictionary<string, string> envVars)
    {
        if (string.IsNullOrEmpty(content) || envVars == null || envVars.Count == 0)
        {
            return content;
        }

        return YamlPlaceholderPattern.Replace(content, match =>
        {
            var varName = match.Groups[1].Value;

            if (envVars.TryGetValue(varName, out var value))
            {
                _logger.LogDebug("Replaced YAML placeholder ${{{VarName}}} with value", varName);
                return value;
            }

            return match.Value;
        });
    }

    /// <summary>
    /// Processes a script file, applying placeholder replacement and header injection.
    /// </summary>
    /// <param name="content">The script content.</param>
    /// <param name="extension">The file extension (e.g., ".ps1").</param>
    /// <param name="envVars">Environment variables for replacement.</param>
    /// <param name="injectHeader">Whether to inject the variable mapping header for PowerShell.</param>
    /// <returns>Processed script content.</returns>
    public string ProcessScript(string content, string extension, IDictionary<string, string> envVars, bool injectHeader = true)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        // Apply placeholder replacement
        var processed = ReplacePlaceholders(content, envVars);

        // For PowerShell scripts in .pkg packages, inject variable mapping header
        if (injectHeader && IsPowerShellScript(extension))
        {
            processed = PowerShellVariableMappingHeader + processed;
            _logger.LogDebug("Injected variable mapping header into PowerShell script");
        }

        return processed;
    }

    /// <summary>
    /// Processes all scripts in a directory, applying placeholder replacement.
    /// </summary>
    /// <param name="sourceDir">Source directory containing scripts.</param>
    /// <param name="destDir">Destination directory for processed scripts.</param>
    /// <param name="envVars">Environment variables for replacement.</param>
    /// <param name="injectHeaders">Whether to inject headers into PowerShell scripts.</param>
    public void ProcessScriptsDirectory(
        string sourceDir,
        string destDir,
        IDictionary<string, string> envVars,
        bool injectHeaders = true)
    {
        if (!Directory.Exists(sourceDir))
        {
            _logger.LogDebug("Scripts directory does not exist: {SourceDir}", sourceDir);
            return;
        }

        Directory.CreateDirectory(destDir);

        foreach (var filePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, filePath);
            var destPath = Path.Combine(destDir, relativePath);
            var destDirPath = Path.GetDirectoryName(destPath);

            if (!string.IsNullOrEmpty(destDirPath))
            {
                Directory.CreateDirectory(destDirPath);
            }

            var extension = Path.GetExtension(filePath);
            var content = File.ReadAllText(filePath, Encoding.UTF8);

            if (IsScriptFile(extension))
            {
                content = ProcessScript(content, extension, envVars, injectHeaders);
                _logger.LogDebug("Processed script: {RelativePath}", relativePath);
            }

            File.WriteAllText(destPath, content, Encoding.UTF8);
        }
    }

    /// <summary>
    /// Gets all preinstall script files from a scripts directory.
    /// Returns files matching preinstall*.ps1 pattern, sorted alphabetically.
    /// </summary>
    /// <param name="scriptsDir">The scripts directory to search.</param>
    /// <returns>Ordered list of preinstall script paths.</returns>
    public IReadOnlyList<string> GetPreinstallScripts(string scriptsDir)
    {
        if (!Directory.Exists(scriptsDir))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(scriptsDir, "preinstall*.ps1", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Gets all postinstall script files from a scripts directory.
    /// Returns files matching postinstall*.ps1 pattern, sorted alphabetically.
    /// </summary>
    /// <param name="scriptsDir">The scripts directory to search.</param>
    /// <returns>Ordered list of postinstall script paths.</returns>
    public IReadOnlyList<string> GetPostinstallScripts(string scriptsDir)
    {
        if (!Directory.Exists(scriptsDir))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(scriptsDir, "postinstall*.ps1", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Gets the uninstall script if it exists.
    /// </summary>
    /// <param name="scriptsDir">The scripts directory to search.</param>
    /// <returns>Path to uninstall.ps1 if it exists, null otherwise.</returns>
    public string? GetUninstallScript(string scriptsDir)
    {
        if (!Directory.Exists(scriptsDir))
        {
            return null;
        }

        var uninstallPath = Path.Combine(scriptsDir, "uninstall.ps1");
        return File.Exists(uninstallPath) ? uninstallPath : null;
    }

    /// <summary>
    /// Combines multiple script files into a single script content.
    /// </summary>
    /// <param name="scriptPaths">Paths to script files to combine.</param>
    /// <param name="envVars">Environment variables for placeholder replacement.</param>
    /// <returns>Combined script content.</returns>
    public string CombineScripts(IEnumerable<string> scriptPaths, IDictionary<string, string> envVars)
    {
        var combined = new StringBuilder();

        foreach (var scriptPath in scriptPaths)
        {
            if (!File.Exists(scriptPath))
            {
                _logger.LogWarning("Script file not found: {ScriptPath}", scriptPath);
                continue;
            }

            var content = File.ReadAllText(scriptPath, Encoding.UTF8);
            content = ReplacePlaceholders(content, envVars);

            combined.AppendLine($"# === Included from: {Path.GetFileName(scriptPath)} ===");
            combined.AppendLine(content);
            combined.AppendLine();
        }

        return combined.ToString();
    }

    /// <summary>
    /// Determines if a file extension represents a PowerShell script.
    /// </summary>
    public static bool IsPowerShellScript(string extension)
    {
        return PowerShellExtensions.Contains(extension);
    }

    /// <summary>
    /// Determines if a file extension represents a script that supports placeholder replacement.
    /// </summary>
    public static bool IsScriptFile(string extension)
    {
        return ScriptExtensions.Contains(extension);
    }
}
