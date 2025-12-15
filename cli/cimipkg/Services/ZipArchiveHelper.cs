using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Cimian.CLI.Cimipkg.Services;

/// <summary>
/// Utilities for creating and manipulating ZIP archives for .pkg packages.
/// Supports ZIP64 for files larger than 2GB.
/// </summary>
public class ZipArchiveHelper
{
    private readonly ILogger<ZipArchiveHelper> _logger;

    public ZipArchiveHelper(ILogger<ZipArchiveHelper> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates a ZIP archive from a source directory.
    /// Uses native .NET ZIP with ZIP64 support (handles files > 2GB).
    /// </summary>
    /// <param name="sourceDir">Source directory to archive.</param>
    /// <param name="zipPath">Path to the output ZIP file.</param>
    public void CreateArchive(string sourceDir, string zipPath)
    {
        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");
        }

        // Ensure destination directory exists
        var destDir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        // Delete existing file if present
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false);

        var files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories);
        var dirs = Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories);

        // Add directories first (for proper structure)
        foreach (var dir in dirs)
        {
            var relativePath = Path.GetRelativePath(sourceDir, dir);
            var entryName = relativePath.Replace('\\', '/') + "/";
            archive.CreateEntry(entryName);
            _logger.LogDebug("Added directory: {EntryName}", entryName);
        }

        // Add files
        foreach (var filePath in files)
        {
            var relativePath = Path.GetRelativePath(sourceDir, filePath);
            var entryName = relativePath.Replace('\\', '/');

            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

            // Preserve file modification time
            var fileInfo = new FileInfo(filePath);
            entry.LastWriteTime = fileInfo.LastWriteTime;

            using var fileStream = File.OpenRead(filePath);
            using var entryStream = entry.Open();
            fileStream.CopyTo(entryStream);

            _logger.LogDebug("Added file: {EntryName} ({Size} bytes)", entryName, fileInfo.Length);
        }

        _logger.LogInformation("Created archive: {ZipPath}", zipPath);
    }

    /// <summary>
    /// Extracts a ZIP archive to a destination directory.
    /// </summary>
    /// <param name="zipPath">Path to the ZIP file.</param>
    /// <param name="destDir">Destination directory.</param>
    public void ExtractArchive(string zipPath, string destDir)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException($"ZIP file not found: {zipPath}");
        }

        Directory.CreateDirectory(destDir);
        ZipFile.ExtractToDirectory(zipPath, destDir, overwriteFiles: true);
        _logger.LogInformation("Extracted archive to: {DestDir}", destDir);
    }

    /// <summary>
    /// Extracts a single file from a ZIP archive.
    /// </summary>
    /// <param name="zipPath">Path to the ZIP file.</param>
    /// <param name="entryName">Name of the entry to extract.</param>
    /// <param name="destPath">Destination file path.</param>
    /// <returns>True if the file was extracted, false if not found.</returns>
    public bool ExtractFile(string zipPath, string entryName, string destPath)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException($"ZIP file not found: {zipPath}");
        }

        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry(entryName);
        if (entry == null)
        {
            // Try with normalized path
            var normalizedName = entryName.Replace('\\', '/');
            entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName, normalizedName, StringComparison.OrdinalIgnoreCase));
        }

        if (entry == null)
        {
            _logger.LogWarning("Entry not found in archive: {EntryName}", entryName);
            return false;
        }

        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        entry.ExtractToFile(destPath, overwrite: true);
        _logger.LogDebug("Extracted: {EntryName} -> {DestPath}", entryName, destPath);
        return true;
    }

    /// <summary>
    /// Updates a single file in a ZIP archive without recompressing everything.
    /// </summary>
    /// <param name="zipPath">Path to the ZIP file.</param>
    /// <param name="entryName">Name of the entry to update.</param>
    /// <param name="sourcePath">Path to the source file.</param>
    public void UpdateFile(string zipPath, string entryName, string sourcePath)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException($"ZIP file not found: {zipPath}");
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Source file not found: {sourcePath}");
        }

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update);

        // Find and delete existing entry
        var normalizedName = entryName.Replace('\\', '/');
        var existingEntry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, normalizedName, StringComparison.OrdinalIgnoreCase));

        existingEntry?.Delete();

        // Add the new file
        archive.CreateEntryFromFile(sourcePath, normalizedName, CompressionLevel.Optimal);
        _logger.LogDebug("Updated entry: {EntryName}", entryName);
    }

    /// <summary>
    /// Calculates a content hash from ZIP entries (excluding build-info.yaml).
    /// Used for package signature verification.
    /// </summary>
    /// <param name="zipPath">Path to the ZIP file.</param>
    /// <returns>SHA256 hash of all content.</returns>
    public string CalculateContentHash(string zipPath)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException($"ZIP file not found: {zipPath}");
        }

        using var archive = ZipFile.OpenRead(zipPath);
        var hashes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var sha256 = SHA256.Create();

        foreach (var entry in archive.Entries)
        {
            // Skip directories and build-info.yaml
            if (string.IsNullOrEmpty(entry.Name) ||
                entry.FullName.Equals("build-info.yaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = entry.Open();
            var hash = sha256.ComputeHash(stream);
            var hashString = Convert.ToHexString(hash).ToLowerInvariant();
            hashes[entry.FullName] = hashString;
        }

        // Combine all hashes into a single string and hash again
        var combined = new StringBuilder();
        foreach (var kvp in hashes)
        {
            combined.Append(kvp.Key);
            combined.Append(':');
            combined.Append(kvp.Value);
            combined.Append('|');
        }

        var finalHash = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined.ToString()));
        return Convert.ToHexString(finalHash).ToLowerInvariant();
    }

    /// <summary>
    /// Lists all entries in a ZIP archive.
    /// </summary>
    /// <param name="zipPath">Path to the ZIP file.</param>
    /// <returns>List of entry information.</returns>
    public IReadOnlyList<ZipEntryInfo> ListEntries(string zipPath)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException($"ZIP file not found: {zipPath}");
        }

        using var archive = ZipFile.OpenRead(zipPath);
        return archive.Entries
            .Select(e => new ZipEntryInfo
            {
                FullName = e.FullName,
                Name = e.Name,
                CompressedLength = e.CompressedLength,
                Length = e.Length,
                LastWriteTime = e.LastWriteTime,
                IsDirectory = string.IsNullOrEmpty(e.Name)
            })
            .ToList();
    }
}

/// <summary>
/// Information about a ZIP archive entry.
/// </summary>
public record ZipEntryInfo
{
    public string FullName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public long CompressedLength { get; init; }
    public long Length { get; init; }
    public DateTimeOffset LastWriteTime { get; init; }
    public bool IsDirectory { get; init; }
}
