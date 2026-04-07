using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using WixToolset.Dtf.WindowsInstaller;

namespace Cimian.CLI.Makepkginfo.Services;

/// <summary>
/// Service for extracting metadata from installer files (MSI, EXE, NUPKG)
/// Migrated from Go: pkg/extract package
/// </summary>
public class MetadataExtractor
{
    /// <summary>
    /// MSI metadata extraction result
    /// </summary>
    public record MsiMetadata(
        string ProductName,
        string ProductVersion,
        string Developer,
        string Description,
        string ProductCode,
        string UpgradeCode);

    /// <summary>
    /// NUPKG metadata extraction result
    /// </summary>
    public record NupkgMetadata(
        string Identifier,
        string Name,
        string Version,
        string Developer,
        string Description);

    /// <summary>
    /// Extracts metadata from an MSI file using DTF (direct msi.dll interop).
    /// </summary>
    public MsiMetadata ExtractMsiMetadata(string msiPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new MsiMetadata("UnknownMSI", "", "", "", "", "");
        }

        try
        {
            using var db = new Database(msiPath, DatabaseOpenMode.ReadOnly);

            string? ReadProp(string name) {
                try { return db.ExecuteScalar($"SELECT `Value` FROM `Property` WHERE `Property` = '{name}'")?.ToString(); }
                catch { return null; }
            }

            var productName = ReadProp("ProductName")?.Trim() ?? "UnknownMSI";
            if (string.IsNullOrEmpty(productName)) productName = "UnknownMSI";

            return new MsiMetadata(
                productName,
                ReadProp("ProductVersion")?.Trim() ?? "",
                ReadProp("Manufacturer")?.Trim() ?? "",
                ReadProp("Comments")?.Trim() ?? "",
                ReadProp("ProductCode")?.Trim() ?? "",
                ReadProp("UpgradeCode")?.Trim() ?? ""
            );
        }
        catch
        {
            return new MsiMetadata("UnknownMSI", "", "", "", "", "");
        }
    }

    /// <summary>
    /// Extracts file version from an EXE file using FileVersionInfo.
    /// Uses numeric version parts (Major.Minor.Build.Revision) to match Go behavior.
    /// </summary>
    public string? ExtractExeVersion(string exePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
            
            // Use numeric parts first (matches Go behavior with FileVersionMS/LS)
            // This produces clean versions like "6.2.26100.5074" instead of strings like
            // "10.0.26100.1 (WinBuild.160101.0800)"
            if (versionInfo.FileMajorPart > 0 || versionInfo.FileMinorPart > 0 || 
                versionInfo.FileBuildPart > 0 || versionInfo.FilePrivatePart > 0)
            {
                return $"{versionInfo.FileMajorPart}.{versionInfo.FileMinorPart}.{versionInfo.FileBuildPart}.{versionInfo.FilePrivatePart}";
            }

            // Fall back to FileVersion string if numeric parts are all zero
            if (!string.IsNullOrEmpty(versionInfo.FileVersion))
            {
                // Try to clean up the version string (remove trailing info in parentheses)
                var version = versionInfo.FileVersion;
                var parenIndex = version.IndexOf('(');
                if (parenIndex > 0)
                {
                    version = version[..parenIndex].Trim();
                }
                return version;
            }
            
            // Last resort: ProductVersion
            if (!string.IsNullOrEmpty(versionInfo.ProductVersion))
            {
                return versionInfo.ProductVersion;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts metadata from a NuGet package (.nupkg) file
    /// </summary>
    public NupkgMetadata ExtractNupkgMetadata(string nupkgPath)
    {
        var fallbackName = Path.GetFileNameWithoutExtension(nupkgPath);

        try
        {
            using var archive = ZipFile.OpenRead(nupkgPath);
            
            // Find the .nuspec file
            var nuspecEntry = archive.Entries.FirstOrDefault(e => 
                e.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));

            if (nuspecEntry == null)
            {
                return new NupkgMetadata(fallbackName, fallbackName, "", "", "");
            }

            using var stream = nuspecEntry.Open();
            var doc = XDocument.Load(stream);
            
            // Handle namespaces in nuspec
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var metadata = doc.Root?.Element(ns + "metadata");

            if (metadata == null)
            {
                return new NupkgMetadata(fallbackName, fallbackName, "", "", "");
            }

            var id = metadata.Element(ns + "id")?.Value?.Trim() ?? fallbackName;
            var title = metadata.Element(ns + "title")?.Value?.Trim();
            var version = metadata.Element(ns + "version")?.Value?.Trim() ?? "";
            var authors = metadata.Element(ns + "authors")?.Value?.Trim() ?? "";
            var description = metadata.Element(ns + "description")?.Value?.Trim() ?? "";

            // Use title if available, otherwise use id
            var name = !string.IsNullOrEmpty(title) ? title : id;

            return new NupkgMetadata(id, name, version, authors, description);
        }
        catch
        {
            return new NupkgMetadata(fallbackName, fallbackName, "", "", "");
        }
    }

    /// <summary>
    /// Calculates SHA256 hash of a file
    /// </summary>
    public string CalculateSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Calculates MD5 hash of a file
    /// </summary>
    public string CalculateMd5(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = MD5.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Gets file size in bytes
    /// </summary>
    public long GetFileSize(string filePath)
    {
        return new FileInfo(filePath).Length;
    }

    /// <summary>
    /// Gets file size in KB
    /// </summary>
    public long GetFileSizeKB(string filePath)
    {
        return GetFileSize(filePath) / 1024;
    }
}
