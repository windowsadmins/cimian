// IBrandingService.cs - Interface for custom client branding

using Microsoft.UI.Xaml.Media.Imaging;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

public interface IBrandingService
{
    /// <summary>
    /// Custom application title, or null for default "Managed Software Center".
    /// </summary>
    string? AppTitle { get; }

    /// <summary>
    /// Custom sidebar header image, or null for no header.
    /// </summary>
    BitmapImage? SidebarHeaderImage { get; }

    /// <summary>
    /// Load branding resources from disk.
    /// </summary>
    Task LoadAsync();
}
