// CatalogCacheService.cs - Local catalog cache for offline browsing
// Implements Munki-style caching: shows "Last updated: X" and graceful offline mode

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Cimian.GUI.ManagedSoftwareCenter.Models;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Service for caching catalog data locally
/// Enables offline browsing like Munki MSC
/// </summary>
public class CatalogCacheService : ICatalogCacheService
{
    private readonly string _cacheDirectory;
    private readonly string _cachePath;
    private readonly string _timestampPath;
    private readonly ILogger<CatalogCacheService>? _logger;

    public CatalogCacheService(ILogger<CatalogCacheService>? logger = null)
    {
        _logger = logger;

        // Use LocalAppData for user-specific cache
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _cacheDirectory = Path.Combine(localAppData, "Cimian", "SoftwareCenter");
        _cachePath = Path.Combine(_cacheDirectory, "InstallInfo.cache.json");
        _timestampPath = Path.Combine(_cacheDirectory, "cache.timestamp");
    }

    /// <inheritdoc />
    public async Task<InstallInfo?> GetCachedInstallInfoAsync()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                _logger?.LogDebug("No cached InstallInfo found");
                return null;
            }

            var json = await File.ReadAllTextAsync(_cachePath);
            var info = JsonSerializer.Deserialize<InstallInfo>(json);

            _logger?.LogDebug("Loaded InstallInfo from cache");
            return info;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load cached InstallInfo");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task CacheInstallInfoAsync(InstallInfo info)
    {
        try
        {
            // Ensure directory exists
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }

            // Serialize and save
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            };
            var json = JsonSerializer.Serialize(info, options);
            await File.WriteAllTextAsync(_cachePath, json);

            // Update timestamp
            await File.WriteAllTextAsync(_timestampPath, DateTime.UtcNow.ToString("O"));

            _logger?.LogDebug("Cached InstallInfo to {Path}", _cachePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to cache InstallInfo");
        }
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetCacheTimestampAsync()
    {
        try
        {
            if (!File.Exists(_timestampPath))
            {
                return null;
            }

            var timestampStr = await File.ReadAllTextAsync(_timestampPath);
            if (DateTime.TryParse(timestampStr, out var timestamp))
            {
                return timestamp.ToLocalTime();
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to read cache timestamp");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task ClearCacheAsync()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                File.Delete(_cachePath);
            }

            if (File.Exists(_timestampPath))
            {
                File.Delete(_timestampPath);
            }

            _logger?.LogDebug("Cleared catalog cache");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to clear cache");
        }
    }

    /// <inheritdoc />
    public string GetLastUpdatedText(DateTime? timestamp)
    {
        if (timestamp == null)
        {
            return "Never checked";
        }

        var elapsed = DateTime.Now - timestamp.Value;

        return elapsed.TotalMinutes switch
        {
            < 1 => "Last checked: just now",
            < 60 => $"Last checked: {(int)elapsed.TotalMinutes} min ago",
            < 1440 => $"Last checked: {(int)elapsed.TotalHours} hours ago",
            _ => $"Last checked: {timestamp.Value:MMM d, h:mm tt}"
        };
    }
}
