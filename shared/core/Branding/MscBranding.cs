using Microsoft.Win32;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.Core.Branding;

/// <summary>
/// <c>branding.yaml</c>, delivered in the <c>client_resources</c> zip. Every key is optional.
/// </summary>
/// <remarks>
/// File names (<c>logo</c>, <c>app_icon</c>, <c>sidebar_header</c>, banner <c>image</c>) are
/// bare names looked up in <c>ManagedInstalls\branding</c> and then
/// <c>ManagedInstalls\client_resources</c>, the two folders the zip fills. Paths are refused,
/// so branding can only show files an administrator put there.
/// </remarks>
public sealed class BrandingDocument
{
    [YamlMember(Alias = "app_title")]
    public string? AppTitle { get; set; }

    /// <summary>Present-but-empty hides the Software page hero title.</summary>
    [YamlMember(Alias = "banner_title")]
    public string? BannerTitle { get; set; }

    [YamlMember(Alias = "accent_color")]
    public string? AccentColor { get; set; }

    /// <summary>Shown in the title bar of Managed Software Center and in CimianStatus.</summary>
    [YamlMember(Alias = "logo")]
    public string? Logo { get; set; }

    /// <summary>An <c>.ico</c> for the window and taskbar.</summary>
    [YamlMember(Alias = "app_icon")]
    public string? AppIcon { get; set; }

    [YamlMember(Alias = "sidebar_header")]
    public string? SidebarHeader { get; set; }

    /// <summary>
    /// The Software page carousel, in order. When absent, every <c>branding*</c> image in the
    /// branding folder is shown in name order, as Munki's default showcase does.
    /// </summary>
    [YamlMember(Alias = "banners")]
    public List<BrandingBannerEntry>? Banners { get; set; }
}

/// <summary>One carousel slide, the counterpart of an <c>&lt;img&gt;</c> in Munki's showcase.</summary>
public sealed class BrandingBannerEntry
{
    [YamlMember(Alias = "image")]
    public string? Image { get; set; }

    /// <summary><c>cimian://…</c>, <c>munki://…</c> or an http(s) URL.</summary>
    [YamlMember(Alias = "href")]
    public string? Href { get; set; }
}

/// <summary>A carousel slide ready to show.</summary>
public sealed record ResolvedBanner(string ImagePath, BrandingLink? Link);

/// <summary>Branding after policy, <c>branding.yaml</c> and defaults have been applied.</summary>
public sealed record MscBranding
{
    public const string DefaultAppTitle = MscBannerTitleResolver.DefaultTitle;

    /// <summary>The custom title, or null for the default.</summary>
    public string? AppTitle { get; init; }

    public string WindowTitle => AppTitle ?? DefaultAppTitle;
    public string BannerTitle { get; init; } = DefaultAppTitle;
    public bool ShowBannerTitle { get; init; } = true;
    public BrandingColor? Accent { get; init; }
    public string? LogoPath { get; init; }
    public string? AppIconPath { get; init; }
    public string? SidebarHeaderPath { get; init; }
    public IReadOnlyList<ResolvedBanner> Banners { get; init; } = [];

    /// <summary>Settings that were ignored, and why. For logs only.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static MscBranding Default { get; } = new();
}

/// <summary>
/// Policy values under <c>HKLM\SOFTWARE\Policies\Cimian</c>. A value that exists wins over
/// <c>branding.yaml</c>; an empty one counts as unset, except <c>BannerTitle</c>, where
/// empty hides the hero title.
/// </summary>
public sealed class BrandingPolicy
{
    public const string RegistryPath = @"SOFTWARE\Policies\Cimian";

    public const string AppTitle = "AppTitle";
    public const string BannerTitle = "BannerTitle";
    public const string AccentColor = "AccentColor";
    public const string Logo = "Logo";
    public const string AppIcon = "AppIcon";
    public const string SidebarHeader = "SidebarHeader";

    private static readonly string[] Names = [AppTitle, BannerTitle, AccentColor, Logo, AppIcon, SidebarHeader];

    private readonly Dictionary<string, string> _values;

    public BrandingPolicy(IEnumerable<KeyValuePair<string, string>>? values = null)
    {
        _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values ?? [])
            _values[name] = value;
    }

    public static BrandingPolicy Empty { get; } = new();

    /// <summary>True when the value exists, even if empty.</summary>
    public bool Has(string name) => _values.ContainsKey(name);

    /// <summary>The value when it exists and is not blank.</summary>
    public string? Get(string name) =>
        _values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    public string? GetRaw(string name) => _values.TryGetValue(name, out var v) ? v : null;

    /// <summary>Reads the branding values from the machine policy key. Never throws.</summary>
    public static BrandingPolicy FromRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath, writable: false);
            if (key == null)
                return Empty;

            var present = key.GetValueNames();
            var values = new List<KeyValuePair<string, string>>();
            foreach (var name in Names)
            {
                var actual = present.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                if (actual == null)
                    continue;
                var raw = key.GetValue(actual);
                values.Add(new(name, raw switch
                {
                    null => string.Empty,
                    string[] lines => string.Join(" ", lines),
                    _ => raw.ToString() ?? string.Empty,
                }));
            }
            return new BrandingPolicy(values);
        }
        catch
        {
            return Empty;
        }
    }
}

/// <summary>
/// Turns policy, <c>branding.yaml</c> and the files on disk into <see cref="MscBranding"/>.
/// Shared by Managed Software Center and CimianStatus so both apps brand the same way.
/// </summary>
public static class MscBrandingResolver
{
    public const string BrandingFileName = "branding.yaml";
    public const int MaxBanners = 10;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];
    private static readonly string[] IconExtensions = [".ico"];

    /// <summary>Folders searched for branding files, first match wins.</summary>
    public static IReadOnlyList<string> DefaultSearchDirectories { get; } =
        [CimianPaths.BrandingDir, CimianPaths.ClientResourcesDir];

    /// <summary>Reads policy and <c>branding.yaml</c> from this machine. Never throws.</summary>
    public static MscBranding Load() =>
        Load(BrandingPolicy.FromRegistry(), DefaultSearchDirectories);

    /// <summary>
    /// Reads <c>branding.yaml</c> from the search folders (client_resources first, where the
    /// zip has always put it) and resolves it against <paramref name="policy"/>. Never throws.
    /// </summary>
    public static MscBranding Load(BrandingPolicy policy, IReadOnlyList<string> searchDirectories)
    {
        var warnings = new List<string>();
        BrandingDocument? document = null;

        foreach (var dir in searchDirectories.Reverse())
        {
            var path = Path.Combine(dir, BrandingFileName);
            if (!File.Exists(path))
                continue;
            try
            {
                document = Parse(File.ReadAllText(path));
                break;
            }
            catch (Exception ex)
            {
                warnings.Add($"{path} could not be read: {ex.Message}");
            }
        }

        var resolved = Resolve(document, policy, searchDirectories);
        return warnings.Count == 0 ? resolved : resolved with { Warnings = [.. warnings, .. resolved.Warnings] };
    }

    /// <summary>Parses <c>branding.yaml</c>; unknown keys are ignored.</summary>
    public static BrandingDocument? Parse(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<BrandingDocument?>(yaml);
    }

    public static MscBranding Resolve(
        BrandingDocument? document,
        BrandingPolicy policy,
        IReadOnlyList<string> searchDirectories)
    {
        document ??= new BrandingDocument();
        var warnings = new List<string>();

        var appTitle = policy.Get(BrandingPolicy.AppTitle) ?? Blank(document.AppTitle);

        var bannerExplicit = policy.Has(BrandingPolicy.BannerTitle) || document.BannerTitle != null;
        var bannerRaw = policy.Has(BrandingPolicy.BannerTitle)
            ? policy.GetRaw(BrandingPolicy.BannerTitle)
            : document.BannerTitle;
        var bannerTitle = MscBannerTitleResolver.Resolve(appTitle, bannerRaw, bannerExplicit);

        BrandingColor? accent = null;
        foreach (var (source, value) in new[]
                 {
                     ($"policy {BrandingPolicy.AccentColor}", policy.Get(BrandingPolicy.AccentColor)),
                     ("accent_color", Blank(document.AccentColor)),
                 })
        {
            if (value == null)
                continue;
            if (BrandingColor.TryParse(value, out var parsed))
            {
                accent = parsed;
                break;
            }
            warnings.Add($"{source} \"{value}\" is not a #RRGGBB colour");
        }

        string? File(string policyName, string? yamlName, string[] extensions, string label)
        {
            foreach (var (source, name) in new[]
                     {
                         ($"policy {policyName}", policy.Get(policyName)),
                         (label, Blank(yamlName)),
                     })
            {
                if (name == null)
                    continue;
                var path = FindFile(name, extensions, searchDirectories, out var problem);
                if (path != null)
                    return path;
                warnings.Add($"{source} \"{name}\" {problem}");
            }
            return null;
        }

        var logo = File(BrandingPolicy.Logo, document.Logo, ImageExtensions, "logo");
        var icon = File(BrandingPolicy.AppIcon, document.AppIcon, IconExtensions, "app_icon");
        var header = File(BrandingPolicy.SidebarHeader, document.SidebarHeader, ImageExtensions, "sidebar_header")
                     ?? FindFile("sidebar_header.png", ImageExtensions, searchDirectories, out _)
                     ?? FindFile("sidebar_header.jpg", ImageExtensions, searchDirectories, out _);

        var banners = ResolveBanners(document.Banners, searchDirectories, warnings);

        // Munki's showcase never draws text over banner images, and a default title on
        // top of artwork collides with whatever the artwork says. Only an explicit
        // banner_title / BannerTitle is drawn over banners.
        var showBanner = MscBannerTitleResolver.ShouldShow(bannerTitle) &&
                         (bannerExplicit || banners.Count == 0);

        return new MscBranding
        {
            AppTitle = appTitle,
            BannerTitle = bannerTitle,
            ShowBannerTitle = showBanner,
            Accent = accent,
            LogoPath = logo,
            AppIconPath = icon,
            SidebarHeaderPath = header,
            Banners = banners,
            Warnings = warnings,
        };
    }

    private static IReadOnlyList<ResolvedBanner> ResolveBanners(
        List<BrandingBannerEntry>? entries,
        IReadOnlyList<string> searchDirectories,
        List<string> warnings)
    {
        var banners = new List<ResolvedBanner>();

        if (entries is { Count: > 0 })
        {
            foreach (var entry in entries)
            {
                if (banners.Count == MaxBanners)
                {
                    warnings.Add($"only the first {MaxBanners} banners are shown");
                    break;
                }
                var name = Blank(entry?.Image);
                if (name == null)
                {
                    warnings.Add("a banner has no image");
                    continue;
                }
                var path = FindFile(name, ImageExtensions, searchDirectories, out var problem);
                if (path == null)
                {
                    warnings.Add($"banner \"{name}\" {problem}");
                    continue;
                }
                var link = BrandingLink.Parse(entry!.Href);
                if (link == null && !string.IsNullOrWhiteSpace(entry.Href) && entry.Href.Trim() != "#")
                    warnings.Add($"banner \"{name}\" link \"{entry.Href}\" is not cimian://, munki:// or http(s)");
                banners.Add(new ResolvedBanner(path, link));
            }
            return banners;
        }

        // Munki's default showcase: every branding* image, in name order.
        foreach (var dir in searchDirectories)
        {
            if (!Directory.Exists(dir))
                continue;
            var found = Directory.EnumerateFiles(dir, "branding*")
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .Take(MaxBanners)
                .Select(f => new ResolvedBanner(f, null))
                .ToList();
            if (found.Count > 0)
                return found;
        }
        return banners;
    }

    /// <summary>
    /// Finds a bare file name in the search folders. Paths, other extensions and names that
    /// resolve outside the folder are refused.
    /// </summary>
    public static string? FindFile(
        string name,
        IReadOnlyCollection<string> extensions,
        IReadOnlyList<string> searchDirectories,
        out string problem)
    {
        problem = "was not found in the branding folders";
        var trimmed = name.Trim();

        if (trimmed.Length == 0 ||
            trimmed.IndexOfAny(['/', '\\', ':']) >= 0 ||
            trimmed is "." or ".." ||
            trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            problem = "must be a file name, not a path";
            return null;
        }

        if (!extensions.Contains(Path.GetExtension(trimmed), StringComparer.OrdinalIgnoreCase))
        {
            problem = $"must be one of {string.Join(", ", extensions)}";
            return null;
        }

        foreach (var dir in searchDirectories)
        {
            var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, trimmed));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            if (System.IO.File.Exists(full))
                return full;
        }
        return null;
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
