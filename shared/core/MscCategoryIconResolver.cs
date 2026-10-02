// MscCategoryIconResolver.cs - Segoe MDL2 category glyphs for Managed Software Center.
// Built-in map + admin overrides from preferences.yaml category_icons.

namespace Cimian.Core;

/// <summary>
/// Resolves category names to Segoe MDL2 Assets glyphs for MSC pills and category cards.
/// Admins can override or extend the map via <c>preferences.yaml</c> <c>category_icons</c>.
/// </summary>
public static class MscCategoryIconResolver
{
    /// <summary>
    /// Generic package/shop glyph used when no built-in or override matches.
    /// </summary>
    public const string DefaultGlyph = "\uE74C";

    // Canonical icon keys (lowercase) → glyph. Aliases are expanded in the static ctor.
    private static readonly Dictionary<string, string> BuiltInGlyphs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["all"] = "\uE8FD",
            ["productivity"] = "\uE7C3",
            ["utilities"] = "\uE90F",
            ["developer tools"] = "\uE943",
            ["communication"] = "\uE8BD",
            ["design"] = "\uE790",
            ["media"] = "\uE8B2",
            ["entertainment"] = "\uE7F4",
            ["business"] = "\uE821",
            ["security"] = "\uE72E",
            ["photo & video"] = "\uE722",
            ["music"] = "\uE8D6",
            ["education"] = "\uE7BE",
            ["gaming"] = "\uE7FC",
            ["docs"] = "\uE8A5",
            ["plugins"] = "\uEA86",
            ["prefs"] = "\uE713",
            ["printing"] = "\uE749",
            ["animation"] = "\uE786",
            ["browsers"] = "\uE774",
            ["modeling"] = "\uE809",
            ["rendering"] = "\uE945",
            ["interactive"] = "\uE815",
            ["video"] = "\uE714",
        };

    // Alias → canonical key (so category_icons: PDF Tools: utilities and overrides work).
    private static readonly Dictionary<string, string> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["developer"] = "developer tools",
            ["dev"] = "developer tools",
            ["development"] = "developer tools",
            ["creativity"] = "design",
            ["management"] = "business",
            ["remediation"] = "security",
            ["documents"] = "docs",
            ["extensions"] = "plugins",
            ["addons"] = "plugins",
            ["preferences"] = "prefs",
            ["browser"] = "browsers",
            ["cad"] = "modeling",
            ["render"] = "rendering",
        };

    static MscCategoryIconResolver()
    {
        // Register aliases as first-class built-in lookups (same glyphs as before).
        foreach (var (alias, canonical) in Aliases)
        {
            if (BuiltInGlyphs.TryGetValue(canonical, out var glyph))
                BuiltInGlyphs[alias] = glyph;
        }
    }

    /// <summary>
    /// Canonical built-in icon key names (no aliases) for admin documentation.
    /// </summary>
    public static IReadOnlyCollection<string> BuiltInIconNames { get; } =
        new[]
        {
            "all", "productivity", "utilities", "developer tools", "communication",
            "design", "media", "entertainment", "business", "security", "photo & video",
            "music", "education", "gaming", "docs", "plugins", "prefs", "printing",
            "animation", "browsers", "modeling", "rendering", "interactive", "video",
        };

    private static IReadOnlyDictionary<string, string> _configuredOverrides =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Installs category→glyph overrides (from preferences.yaml <c>category_icons</c>).
    /// Pass null/empty to clear and use built-ins only.
    /// </summary>
    public static void ConfigureOverrides(IReadOnlyDictionary<string, string>? overrides)
    {
        _configuredOverrides = overrides is { Count: > 0 }
            ? new Dictionary<string, string>(overrides, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a category display name to a Segoe MDL2 glyph string using any
    /// overrides installed via <see cref="ConfigureOverrides"/>.
    /// </summary>
    public static string Resolve(string? category) =>
        Resolve(category, _configuredOverrides);

    /// <summary>
    /// Resolves a category display name to a Segoe MDL2 glyph string.
    /// </summary>
    /// <param name="category">Category name from pkginfo / InstallInfo (e.g. "Browsers", "PDF Tools").</param>
    /// <param name="overrides">
    /// Optional map of category name → already-resolved glyph (from <see cref="BuildOverrideGlyphs"/>).
    /// Matching is case-insensitive on the category key.
    /// </param>
    public static string Resolve(
        string? category,
        IReadOnlyDictionary<string, string>? overrides)
    {
        if (string.IsNullOrWhiteSpace(category))
            return DefaultGlyph;

        var key = category.Trim();

        if (overrides != null &&
            overrides.TryGetValue(key, out var overrideGlyph) &&
            !string.IsNullOrEmpty(overrideGlyph))
        {
            return overrideGlyph;
        }

        // Case-insensitive override lookup when dictionary comparer is not ignore-case.
        if (overrides != null)
        {
            foreach (var pair in overrides)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(pair.Value))
                {
                    return pair.Value;
                }
            }
        }

        if (BuiltInGlyphs.TryGetValue(key, out var builtIn))
            return builtIn;

        return DefaultGlyph;
    }

    /// <summary>
    /// Parses admin icon specs from YAML into a category→glyph map.
    /// Values may be a built-in icon key (<c>utilities</c>), a Segoe hex codepoint
    /// (<c>E90F</c> / <c>0xE90F</c>), or a single Unicode character.
    /// Invalid entries are skipped.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildOverrideGlyphs(
        IReadOnlyDictionary<string, string>? categoryIcons)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (categoryIcons == null || categoryIcons.Count == 0)
            return result;

        foreach (var (category, spec) in categoryIcons)
        {
            if (string.IsNullOrWhiteSpace(category))
                continue;

            if (TryParseIconSpec(spec, out var glyph))
                result[category.Trim()] = glyph;
        }

        return result;
    }

    /// <summary>
    /// Parses one icon spec into a glyph string.
    /// </summary>
    public static bool TryParseIconSpec(string? spec, out string glyph)
    {
        glyph = string.Empty;
        if (string.IsNullOrWhiteSpace(spec))
            return false;

        var value = spec.Trim();

        // Built-in named key (or alias), e.g. "utilities", "browsers".
        if (BuiltInGlyphs.TryGetValue(value, out var named))
        {
            glyph = named;
            return true;
        }

        // Hex codepoint: E90F, 0xE90F, U+E90F.
        if (TryParseHexCodepoint(value, out var codepoint))
        {
            try
            {
                glyph = char.ConvertFromUtf32(codepoint);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        // Raw single Unicode scalar (BMP or surrogate pair as one string element).
        if (IsSingleGlyph(value))
        {
            glyph = value;
            return true;
        }

        return false;
    }

    private static bool TryParseHexCodepoint(string value, out int codepoint)
    {
        codepoint = 0;
        var hex = value;

        if (hex.StartsWith("U+", StringComparison.OrdinalIgnoreCase) ||
            hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }
        else if (hex.StartsWith("&#x", StringComparison.OrdinalIgnoreCase))
        {
            // Tolerate HTML entity form from docs copy-paste (e.g. &#xE90F;).
            hex = hex[3..].TrimEnd(';');
        }

        // Segoe MDL2 PUA is typically 4 hex digits (E000–F8FF). Reject bare words.
        if (hex.Length is < 3 or > 6)
            return false;

        if (!hex.All(char.IsAsciiHexDigit))
            return false;

        return int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out codepoint);
    }

    private static bool IsSingleGlyph(string value)
    {
        var enumCount = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            enumCount++;
            if (enumCount > 1)
                return false;
        }

        return enumCount == 1;
    }
}
