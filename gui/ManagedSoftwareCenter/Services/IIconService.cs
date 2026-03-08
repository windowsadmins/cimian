// IIconService.cs - Interface for loading and caching app icons

using Microsoft.UI.Xaml.Media.Imaging;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Service for loading app icons from the Munki-style icons directory
/// </summary>
public interface IIconService
{
    /// <summary>
    /// Get an icon for the given item name, with optional icon filename hint.
    /// Returns a cached BitmapImage or generates a fallback initials icon.
    /// </summary>
    Task<BitmapImage> GetIconAsync(string itemName, string? iconFileName = null);

    /// <summary>
    /// Clear the in-memory icon cache
    /// </summary>
    void ClearCache();
}
