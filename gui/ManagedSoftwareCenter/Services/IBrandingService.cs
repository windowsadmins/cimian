// IBrandingService.cs - Interface for custom client branding

using Cimian.Core.Branding;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

public interface IBrandingService
{
    /// <summary>
    /// Branding as resolved from policy, branding.yaml and defaults. Read synchronously at
    /// startup so the accent and title are in place before the first window draws.
    /// </summary>
    MscBranding Current { get; }

    /// <summary>
    /// Custom application title, or null for the default chrome title.
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
    /// Custom logo for the title bar, or null for none.
    /// </summary>
    BitmapImage? LogoImage { get; }

    /// <summary>
    /// Re-reads branding from disk and policy. Cheap; safe to call from any page.
    /// </summary>
    MscBranding Reload();

    /// <summary>
    /// Reloads branding and decodes its images. Must run on the UI thread.
    /// </summary>
    Task LoadAsync();

    /// <summary>
    /// Decodes an image file without holding it open, or returns null.
    /// </summary>
    Task<BitmapImage?> LoadImageAsync(string? path);
}
