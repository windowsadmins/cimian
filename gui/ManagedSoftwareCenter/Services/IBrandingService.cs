// IBrandingService.cs - Interface for custom client branding

using Microsoft.UI.Xaml.Media.Imaging;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

public interface IBrandingService
{
    /// <summary>
    /// Custom application title from branding.yaml, or null for default chrome title.
    /// </summary>
    string? AppTitle { get; }

    /// <summary>
    /// Resolved Software-page hero banner title. Empty means hide the overlay.
    /// Precedence: explicit banner_title (YAML/CSP) > app_title > "Managed Software Center".
    /// </summary>
    string ResolvedBannerTitle { get; }

    /// <summary>
    /// True when <see cref="ResolvedBannerTitle"/> should be shown on the hero banner.
    /// </summary>
    bool ShowBannerTitle { get; }

    /// <summary>
    /// Custom sidebar header image, or null for no header.
    /// </summary>
    BitmapImage? SidebarHeaderImage { get; }

    /// <summary>
    /// Load branding resources from disk and apply policy overrides.
    /// </summary>
    Task LoadAsync();
}
