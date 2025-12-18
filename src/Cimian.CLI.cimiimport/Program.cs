using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Cimian.CLI.Cimiimport;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("cimiimport - Placeholder implementation");
        Console.WriteLine("This tool is part of the Cimian C# migration and will be implemented in future phases.");
        
        if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h"))
        {
            Console.WriteLine("Usage: cimiimport [options]");
            Console.WriteLine("This is a placeholder implementation.");
            return 0;
        }
        
        Console.WriteLine("Placeholder execution completed successfully.");
        await Task.Delay(100); // Simulate async work
        return 0;
    }
}

/// <summary>
/// Provides utility methods for detecting architecture from filenames
/// and managing architecture-aware package imports.
/// </summary>
public static class ArchitectureHelper
{
    /// <summary>
    /// Detects the architecture from a filename by looking for common architecture
    /// identifiers like x64, arm64, amd64, etc.
    /// Returns null if no architecture hint is found.
    /// </summary>
    /// <param name="filename">The filename to analyze</param>
    /// <returns>Detected architecture (e.g., "x64", "arm64", "x86") or null if none detected</returns>
    public static string? DetectArchFromFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return null;

        // Convert to lowercase for case-insensitive matching
        var lower = filename.ToLowerInvariant();

        // Check for arm64 first (more specific)
        if (lower.Contains("arm64") || lower.Contains("aarch64"))
        {
            return "arm64";
        }

        // Check for x64/amd64/x86_64
        if (lower.Contains("x64") || lower.Contains("amd64") || 
            lower.Contains("x86_64") || lower.Contains("x86-64"))
        {
            return "x64";
        }

        // Check for x86/win32 (32-bit)
        if (lower.Contains("x86") || lower.Contains("win32") || 
            lower.Contains("i386") || lower.Contains("i686"))
        {
            return "x86";
        }

        return null;
    }

    /// <summary>
    /// Parses a comma/semicolon/space separated architecture string into a list.
    /// </summary>
    /// <param name="archString">Architecture string like "x64,arm64" or "x64 arm64"</param>
    /// <returns>List of normalized architecture identifiers</returns>
    public static List<string> ParseArchitectures(string? archString)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(archString))
            return result;

        // Split on comma, semicolon, space, or tab
        var parts = archString.Split(new[] { ',', ';', ' ', '\t' }, 
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var normalized = part.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    /// <summary>
    /// Determines the supported architectures for a package, giving priority to
    /// architecture detected from the filename over default configuration.
    /// </summary>
    /// <param name="filename">The package filename</param>
    /// <param name="defaultArch">Default architecture from configuration (e.g., "x64,arm64")</param>
    /// <returns>Tuple of (primary architecture, list of supported architectures)</returns>
    public static (string primaryArch, List<string> supportedArchs) DetermineArchitectures(
        string filename, string? defaultArch)
    {
        // First, try to detect architecture from filename
        var detectedArch = DetectArchFromFilename(filename);
        
        if (!string.IsNullOrEmpty(detectedArch))
        {
            // Architecture detected from filename takes priority
            return (detectedArch, new List<string> { detectedArch });
        }

        // Fall back to default configuration
        var archList = ParseArchitectures(defaultArch);
        if (archList.Count == 0)
        {
            // Ultimate fallback: x64
            return ("x64", new List<string> { "x64" });
        }

        return (archList[0], archList);
    }

    /// <summary>
    /// When using an existing package as a template, preserves the detected
    /// architecture from the new package's filename rather than using the
    /// template's architecture.
    /// </summary>
    /// <param name="newFilename">Filename of the new package being imported</param>
    /// <param name="templateArchitectures">Architectures from the template package</param>
    /// <param name="defaultArch">Default architecture from configuration</param>
    /// <returns>Tuple of (primary architecture, list of supported architectures)</returns>
    public static (string primaryArch, List<string> supportedArchs) ResolveTemplateArchitecture(
        string newFilename,
        List<string>? templateArchitectures,
        string? defaultArch)
    {
        // Try to detect architecture from the new filename first
        var detectedArch = DetectArchFromFilename(newFilename);
        
        if (!string.IsNullOrEmpty(detectedArch))
        {
            // New filename has architecture hint - use it instead of template
            return (detectedArch, new List<string> { detectedArch });
        }

        // No architecture in filename - use template's architecture if available
        if (templateArchitectures != null && templateArchitectures.Count > 0)
        {
            return (templateArchitectures[0], templateArchitectures);
        }

        // Fall back to default configuration
        return DetermineArchitectures(newFilename, defaultArch);
    }
}

/// <summary>
/// Holds metadata extracted from an installer package.
/// </summary>
public class InstallerMetadata
{
    public string Title { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Developer { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string UpgradeCode { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public List<string> SupportedArchitectures { get; set; } = new();
    public string InstallerType { get; set; } = string.Empty;
    public List<string> Catalogs { get; set; } = new();
    public string RepoPath { get; set; } = string.Empty;
    public bool UnattendedInstall { get; set; } = true;
    public bool UnattendedUninstall { get; set; } = true;
    public List<string> Requires { get; set; } = new();
    public List<string> UpdateFor { get; set; } = new();

    /// <summary>
    /// Applies architecture detection from filename if not already set.
    /// </summary>
    /// <param name="filename">The installer filename</param>
    /// <param name="defaultArch">Default architecture from configuration</param>
    public void ApplyArchitectureFromFilename(string filename, string? defaultArch)
    {
        var (primary, supported) = ArchitectureHelper.DetermineArchitectures(filename, defaultArch);
        Architecture = primary;
        SupportedArchitectures = supported;
    }

    /// <summary>
    /// Applies template values while preserving detected architecture from the new filename.
    /// </summary>
    /// <param name="template">The template package to copy values from</param>
    /// <param name="newFilename">The filename of the new package being imported</param>
    /// <param name="defaultArch">Default architecture from configuration</param>
    public void ApplyTemplatePreservingArchitecture(InstallerMetadata template, string newFilename, string? defaultArch)
    {
        // Store the extracted version before applying template
        var extractedVersion = Version;
        
        // Store detected architecture before applying template
        var (detectedPrimary, detectedSupported) = ArchitectureHelper.ResolveTemplateArchitecture(
            newFilename, 
            SupportedArchitectures.Count > 0 ? SupportedArchitectures : null,
            defaultArch);

        // Copy template values
        Id = template.Id;
        Title = template.Title;
        Developer = template.Developer;
        Category = template.Category;
        Catalogs = new List<string>(template.Catalogs);
        UnattendedInstall = template.UnattendedInstall;
        UnattendedUninstall = template.UnattendedUninstall;
        Requires = new List<string>(template.Requires);
        UpdateFor = new List<string>(template.UpdateFor);

        // Restore the extracted version (not from template)
        Version = extractedVersion;

        // Update description with new version
        Description = template.Description.Replace(template.Version, extractedVersion);

        // Restore the detected architecture (not from template)
        // This is the key fix - architecture from filename takes priority over template
        Architecture = detectedPrimary;
        SupportedArchitectures = detectedSupported;

        // Optionally preserve repo path from template
        if (!string.IsNullOrEmpty(template.RepoPath))
        {
            RepoPath = template.RepoPath;
        }
    }
}
