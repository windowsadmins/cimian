// BrandingService.cs - Loads custom client branding from client_resources directory.
// Reads branding.yaml for app_title / banner_title, applies Policies\Cimian BannerTitle,
// and loads sidebar_header.png if present.

using System.IO;
using Cimian.Core;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

public sealed class BrandingService : IBrandingService
{
    // Same hive as ConfigurationService.ApplyPolicyOverrides (CimianPrefs / Policy CSP).
    private const string PolicyRegistryPath = @"SOFTWARE\Policies\Cimian";
    private const string BannerTitlePolicyValue = "BannerTitle";

    private static readonly string BrandingDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ManagedInstalls", "client_resources");

    private static readonly string BrandingYamlPath = Path.Combine(BrandingDirectory, "branding.yaml");

    private readonly IDeserializer _deserializer;

    // Raw YAML/policy inputs used to resolve the hero title.
    private string? _bannerTitle;
    private bool _bannerTitleExplicit;

    public string? AppTitle { get; private set; }
    public string ResolvedBannerTitle { get; private set; } = MscBannerTitleResolver.DefaultTitle;
    public bool ShowBannerTitle { get; private set; } = true;
    public BitmapImage? SidebarHeaderImage { get; private set; }

    public BrandingService()
    {
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public async Task LoadAsync()
    {
        AppTitle = null;
        _bannerTitle = null;
        _bannerTitleExplicit = false;
        SidebarHeaderImage = null;

        try
        {
            if (File.Exists(BrandingYamlPath))
            {
                var content = await File.ReadAllTextAsync(BrandingYamlPath);
                var branding = _deserializer.Deserialize<BrandingConfig>(content);
                if (branding != null)
                {
                    AppTitle = branding.AppTitle;
                    if (branding.BannerTitle != null)
                    {
                        _bannerTitle = branding.BannerTitle;
                        _bannerTitleExplicit = true;
                    }
                }
            }
        }
        catch
        {
            // Policy still applies when the optional YAML file cannot be read.
        }

        ApplyBannerTitlePolicyOverride();
        ResolvedBannerTitle = MscBannerTitleResolver.Resolve(
            AppTitle, _bannerTitle, _bannerTitleExplicit);
        ShowBannerTitle = MscBannerTitleResolver.ShouldShow(ResolvedBannerTitle);

        SidebarHeaderImage = await TryLoadImageAsync("sidebar_header.png")
                          ?? await TryLoadImageAsync("sidebar_header.jpg");
    }

    /// <summary>
    /// Reads HKLM\SOFTWARE\Policies\Cimian\BannerTitle when the value name exists.
    /// Empty REG_SZ is a deliberate hide (same as banner_title: "").
    /// </summary>
    private void ApplyBannerTitlePolicyOverride()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyRegistryPath, writable: false);
            if (key == null)
            {
                return;
            }

            // Presence of the value name matters — empty string is still an override.
            var names = key.GetValueNames();
            var hasBannerTitle = names.Any(n =>
                string.Equals(n, BannerTitlePolicyValue, StringComparison.OrdinalIgnoreCase));
            if (!hasBannerTitle)
            {
                return;
            }

            var raw = key.GetValue(BannerTitlePolicyValue);
            _bannerTitleExplicit = true;
            _bannerTitle = raw switch
            {
                string s => s,
                null => string.Empty,
                _ => raw.ToString() ?? string.Empty
            };
        }
        catch
        {
            // Leave YAML (or unset) values in place if policy cannot be read.
        }
    }

    private static async Task<BitmapImage?> TryLoadImageAsync(string filename)
    {
        var fullPath = Path.Combine(BrandingDirectory, filename);
        var resolved = Path.GetFullPath(fullPath);
        // Ensure path stays within branding directory
        if (!resolved.StartsWith(BrandingDirectory, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!File.Exists(resolved))
            return null;

        try
        {
            var bitmap = new BitmapImage();
            using var stream = File.OpenRead(resolved);
            var memStream = new MemoryStream();
            await stream.CopyToAsync(memStream);
            memStream.Position = 0;
            await bitmap.SetSourceAsync(memStream.AsRandomAccessStream());
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private class BrandingConfig
    {
        [YamlMember(Alias = "app_title")]
        public string? AppTitle { get; set; }

        // Non-null after deserialize means the YAML key was present ("" hides the hero).
        [YamlMember(Alias = "banner_title")]
        public string? BannerTitle { get; set; }
    }
}
