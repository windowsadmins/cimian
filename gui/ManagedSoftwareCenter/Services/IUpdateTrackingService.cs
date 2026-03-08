// IUpdateTrackingService.cs - Interface for tracking when updates were first discovered

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Tracks when updates were first discovered to show "pending for X days" warnings
/// </summary>
public interface IUpdateTrackingService
{
    /// <summary>
    /// Records that an item was seen as pending. Only stores the first-seen date.
    /// </summary>
    Task TrackItemAsync(string itemName);

    /// <summary>
    /// Gets the number of days an item has been pending, or null if not tracked
    /// </summary>
    Task<int?> GetDaysPendingAsync(string itemName);

    /// <summary>
    /// Removes tracking for an item (e.g., after it's installed)
    /// </summary>
    Task RemoveItemAsync(string itemName);

    /// <summary>
    /// Prunes items no longer in the pending list
    /// </summary>
    Task PruneAsync(IEnumerable<string> currentPendingItems);
}
