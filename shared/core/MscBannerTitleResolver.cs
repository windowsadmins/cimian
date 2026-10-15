// MscBannerTitleResolver.cs - Pure resolution for the MSC Software hero banner title.
// Precedence: explicit banner_title (incl. blank) > non-empty app_title > default.

namespace Cimian.Core;

/// <summary>
/// Resolves the Managed Software Center Software-page hero banner heading.
/// </summary>
public static class MscBannerTitleResolver
{
    /// <summary>
    /// Default hero text when neither <c>banner_title</c> nor <c>app_title</c> applies.
    /// </summary>
    public const string DefaultTitle = "Managed Software Center";

    /// <summary>
    /// Resolves the hero banner title string.
    /// </summary>
    /// <param name="appTitle">
    /// Value of <c>app_title</c> from branding.yaml (null/whitespace means unset).
    /// </param>
    /// <param name="bannerTitle">
    /// Value of <c>banner_title</c> / policy <c>BannerTitle</c> when explicitly set.
    /// Empty string means "hide the overlay".
    /// </param>
    /// <param name="bannerTitleExplicit">
    /// True when YAML included <c>banner_title</c> or policy defined <c>BannerTitle</c>
    /// (presence matters; blank is a deliberate hide).
    /// </param>
    /// <returns>
    /// Display text for the hero overlay. Empty/whitespace means the overlay should be hidden.
    /// </returns>
    public static string Resolve(string? appTitle, string? bannerTitle, bool bannerTitleExplicit)
    {
        // Explicit banner_title always wins — including blank to hide the overlay.
        if (bannerTitleExplicit)
        {
            return bannerTitle?.Trim() ?? string.Empty;
        }

        // Fall back to app_title when set (same string used for the window title bar).
        if (!string.IsNullOrWhiteSpace(appTitle))
        {
            return appTitle.Trim();
        }

        return DefaultTitle;
    }

    /// <summary>
    /// Whether the hero overlay TextBlock should be visible for a resolved title.
    /// </summary>
    public static bool ShouldShow(string? resolvedTitle) =>
        !string.IsNullOrWhiteSpace(resolvedTitle);
}
