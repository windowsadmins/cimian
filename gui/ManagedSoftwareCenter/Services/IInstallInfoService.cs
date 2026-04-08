// IInstallInfoService.cs - Interface for InstallInfo operations

using Cimian.GUI.ManagedSoftwareCenter.Models;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Service for reading and caching InstallInfo data (like Munki's InstallInfo.plist)
/// </summary>
public interface IInstallInfoService
{
    /// <summary>
    /// Load InstallInfo from disk
    /// </summary>
    Task<InstallInfo> LoadAsync();

    /// <summary>
    /// Get optional installable items
    /// </summary>
    Task<IReadOnlyList<InstallableItem>> GetOptionalInstallsAsync();

    /// <summary>
    /// Get items scheduled for installation
    /// </summary>
    Task<IReadOnlyList<InstallableItem>> GetManagedInstallsAsync();

    /// <summary>
    /// Get items scheduled for removal
    /// </summary>
    Task<IReadOnlyList<InstallableItem>> GetRemovalsAsync();

    /// <summary>
    /// Get available updates
    /// </summary>
    Task<IReadOnlyList<InstallableItem>> GetManagedUpdatesAsync();

    /// <summary>
    /// Get problem items
    /// </summary>
    Task<IReadOnlyList<ProblemItem>> GetProblemItemsAsync();

    /// <summary>
    /// Get all items across every list (optional + processed + managed + updates).
    /// Used internally for lookups — not for the Software browse page.
    /// </summary>
    Task<IReadOnlyList<InstallableItem>> GetAllItemsAsync();

    /// <summary>
    /// Get browseable items for the Software page (optional + processed only).
    /// Matches Munki behavior: managed_installs are admin-forced and hidden from the catalog.
    /// </summary>
    Task<IReadOnlyList<InstallableItem>> GetBrowseableItemsAsync();

    /// <summary>
    /// Get all unique categories
    /// </summary>
    Task<IReadOnlyList<string>> GetCategoriesAsync();

    /// <summary>
    /// Get items by category
    /// </summary>
    Task<IReadOnlyList<InstallableItem>> GetItemsByCategoryAsync(string category);

    /// <summary>
    /// Get a specific item by name
    /// </summary>
    Task<InstallableItem?> GetItemByNameAsync(string name);

    /// <summary>
    /// Get the last check timestamp
    /// </summary>
    Task<DateTime?> GetLastCheckTimeAsync();

    /// <summary>
    /// Event raised when InstallInfo changes
    /// </summary>
    event EventHandler<InstallInfo>? InstallInfoChanged;

    /// <summary>
    /// Start watching for InstallInfo file changes
    /// </summary>
    void StartWatching();

    /// <summary>
    /// Stop watching for changes
    /// </summary>
    void StopWatching();
}
