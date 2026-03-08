// IPreferencesService.cs - Interface for reading MSC preferences

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Service for reading Managed Software Center preferences from preferences.yaml
/// </summary>
public interface IPreferencesService
{
    /// <summary>
    /// Number of days after which to enter aggressive/obnoxious notification mode (default: 14)
    /// </summary>
    int AggressiveNotificationDays { get; }

    /// <summary>
    /// URL to open when user clicks Help
    /// </summary>
    string? HelpUrl { get; }

    /// <summary>
    /// Ordered list of sidebar items to show. Supported values: "software", "categories", "myitems", "updates".
    /// Null means use the default set.
    /// </summary>
    List<string>? SidebarItems { get; }

    /// <summary>
    /// Reload preferences from disk
    /// </summary>
    Task ReloadAsync();
}
