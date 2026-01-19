using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core.Services;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Service for downloading packages with hash verification
/// Migrated from Go pkg/download
/// </summary>
public class DownloadService
{
    private readonly HttpClient _httpClient;
    private readonly CimianConfig _config;

    public DownloadService(CimianConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _httpClient = httpClient ?? CreateHttpClient(config);
    }

    private static HttpClient CreateHttpClient(CimianConfig config)
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30) // Long timeout for large downloads
        };

        if (!string.IsNullOrEmpty(config.AuthToken))
        {
            client.DefaultRequestHeaders.Authorization = 
                new AuthenticationHeaderValue("Bearer", config.AuthToken);
        }
        else if (!string.IsNullOrEmpty(config.AuthUser) && !string.IsNullOrEmpty(config.AuthPassword))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{config.AuthUser}:{config.AuthPassword}"));
            client.DefaultRequestHeaders.Authorization = 
                new AuthenticationHeaderValue("Basic", credentials);
        }

        client.DefaultRequestHeaders.Add("User-Agent", "Cimian-ManagedSoftwareUpdate/1.0");

        return client;
    }

    /// <summary>
    /// Downloads a file from URL to local path
    /// </summary>
    public async Task<bool> DownloadFileAsync(
        string url,
        string localPath,
        string? expectedHash = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        ConsoleLogger.Detail($"    Starting download url: {url}");

        // Check if file exists and matches hash
        if (File.Exists(localPath) && !string.IsNullOrEmpty(expectedHash))
        {
            ConsoleLogger.Detail($"    Verifying cached file: {localPath}");
            var existingHash = CalculateSHA256(localPath);
            if (existingHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                ConsoleLogger.Info($"Using cached file: {Path.GetFileName(localPath)}");
                ConsoleLogger.Detail($"    Hash verification passed for cached file: {localPath}");
                return true;
            }
            ConsoleLogger.Detail($"    Cached file hash mismatch, re-downloading expected: {expectedHash.Substring(0, 12)}... got: {existingHash.Substring(0, 12)}...");
        }

        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var tempPath = localPath + ".tmp";
            ConsoleLogger.Detail($"    Download started size: {totalBytes} bytes dest: {tempPath}");

            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            using (var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                var buffer = new byte[81920];
                long totalBytesRead = 0;
                int bytesRead;

                while ((bytesRead = await httpStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalBytesRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        progress?.Report((double)totalBytesRead / totalBytes * 100);
                    }
                }
            }
            ConsoleLogger.Detail($"    Download completed to temp file tempFile: {tempPath}");

            // Verify hash if provided
            if (!string.IsNullOrEmpty(expectedHash))
            {
                ConsoleLogger.Detail($"    Verifying hash for downloaded file: {tempPath}");
                var downloadedHash = CalculateSHA256(tempPath);
                if (!downloadedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tempPath);
                    ConsoleLogger.Error($"Hash mismatch for {url}");
                    ConsoleLogger.Error($"        Expected: {expectedHash}");
                    Console.Error.WriteLine($"        Got:      {downloadedHash}");
                    return false;
                }
                ConsoleLogger.Detail($"    Hash verification passed for: {tempPath}");
            }

            // Move temp file to final location
            if (File.Exists(localPath))
            {
                File.Delete(localPath);
            }
            File.Move(tempPath, localPath);
            ConsoleLogger.Detail($"    File saved successfully file: {localPath}");

            return true;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"Failed to download {url}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Downloads a catalog item's installer
    /// </summary>
    public async Task<string?> DownloadItemAsync(
        CatalogItem item,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(item.Installer.Location))
        {
            // Script-only item
            return null;
        }

        var url = BuildFullUrl(item.Installer.Location);
        var localPath = GetCachePath(item);

        var success = await DownloadFileAsync(
            url,
            localPath,
            item.Installer.Hash,
            progress,
            cancellationToken);

        return success ? localPath : null;
    }

    /// <summary>
    /// Downloads multiple items
    /// </summary>
    public async Task<Dictionary<string, string>> DownloadItemsAsync(
        IEnumerable<CatalogItem> items,
        IProgress<(string ItemName, double Percent)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>();
        var itemList = items.ToList();
        var count = 0;

        foreach (var item in itemList)
        {
            count++;
            var itemProgress = new Progress<double>(p =>
            {
                progress?.Report((item.Name, p));
            });

            var path = await DownloadItemAsync(item, itemProgress, cancellationToken);
            if (!string.IsNullOrEmpty(path))
            {
                result[item.Name] = path;
            }

            ConsoleLogger.Info($"Downloaded {count}/{itemList.Count}: {item.Name}");
        }

        return result;
    }

    /// <summary>
    /// Builds full URL from location
    /// </summary>
    public string BuildFullUrl(string location)
    {
        if (location.StartsWith("http://") || location.StartsWith("https://"))
        {
            return location;
        }

        var normalizedLocation = location.Replace("\\", "/");
        if (!normalizedLocation.StartsWith("/"))
        {
            normalizedLocation = "/" + normalizedLocation;
        }

        return $"{_config.SoftwareRepoURL.TrimEnd('/')}/pkgs{normalizedLocation}";
    }

    /// <summary>
    /// Gets the local cache path for an item
    /// </summary>
    public string GetCachePath(CatalogItem item)
    {
        var fileName = Path.GetFileName(item.Installer.Location);
        
        // Organize by category if available
        if (!string.IsNullOrEmpty(item.Category))
        {
            var categoryPath = item.Category.Replace(" ", "_").ToLowerInvariant();
            return Path.Combine(_config.CachePath, categoryPath, fileName);
        }

        return Path.Combine(_config.CachePath, fileName);
    }

    /// <summary>
    /// Calculates SHA256 hash of a file
    /// </summary>
    public static string CalculateSHA256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Validates cache integrity and removes corrupt files
    /// </summary>
    public void ValidateAndCleanCache()
    {
        if (!Directory.Exists(_config.CachePath))
        {
            return;
        }

        var files = Directory.GetFiles(_config.CachePath, "*", SearchOption.AllDirectories);
        var corruptCount = 0;

        foreach (var file in files)
        {
            var info = new FileInfo(file);
            
            // Remove zero-byte files
            if (info.Length == 0)
            {
                try
                {
                    File.Delete(file);
                    corruptCount++;
                    ConsoleLogger.Info($"Removed corrupt file: {file}");
                }
                catch (Exception ex)
                {
                    ConsoleLogger.Warn($"Failed to remove corrupt file {file}: {ex.Message}");
                }
            }
        }

        if (corruptCount > 0)
        {
            ConsoleLogger.Info($"Removed {corruptCount} corrupt files from cache");
        }
    }

    /// <summary>
    /// Gets cache statistics
    /// </summary>
    public (int FileCount, long TotalSize, int CorruptCount) GetCacheStatus()
    {
        if (!Directory.Exists(_config.CachePath))
        {
            return (0, 0, 0);
        }

        var files = Directory.GetFiles(_config.CachePath, "*", SearchOption.AllDirectories);
        var totalSize = 0L;
        var corruptCount = 0;

        foreach (var file in files)
        {
            var info = new FileInfo(file);
            totalSize += info.Length;
            if (info.Length == 0)
            {
                corruptCount++;
            }
        }

        return (files.Length, totalSize, corruptCount);
    }

    /// <summary>
    /// Clears the cache selectively based on successful installations
    /// </summary>
    public void ClearCacheSelective(HashSet<string> successfullyInstalled)
    {
        if (!Directory.Exists(_config.CachePath))
        {
            return;
        }

        var files = Directory.GetFiles(_config.CachePath, "*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            
            // Check if this file is for a successfully installed item
            var shouldRemove = successfullyInstalled.Any(item =>
                fileName.StartsWith(item, StringComparison.OrdinalIgnoreCase));

            if (shouldRemove)
            {
                try
                {
                    File.Delete(file);
                    ConsoleLogger.Info($"Removed cached file: {fileName}");
                }
                catch (Exception ex)
                {
                    ConsoleLogger.Warn($"Failed to remove cached file {fileName}: {ex.Message}");
                }
            }
        }
    }
}
