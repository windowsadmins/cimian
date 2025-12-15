// ICatalogCacheService.cs - Interface for local catalog caching

using Cimian.GUI.ManagedSoftwareCenter.Models;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Service for caching catalog data locally (Munki-style offline browsing)
/// </summary>
public interface ICatalogCacheService
{
    /// <summary>
    /// Get cached InstallInfo, if available
    /// </summary>
    Task<InstallInfo?> GetCachedInstallInfoAsync();

    /// <summary>
    /// Save InstallInfo to local cache
    /// </summary>
    Task CacheInstallInfoAsync(InstallInfo info);

    /// <summary>
    /// Get the cache timestamp
    /// </summary>
    Task<DateTime?> GetCacheTimestampAsync();

    /// <summary>
    /// Clear the local cache
    /// </summary>
    Task ClearCacheAsync();

    /// <summary>
    /// Get formatted "last updated" text
    /// </summary>
    string GetLastUpdatedText(DateTime? timestamp);
}
