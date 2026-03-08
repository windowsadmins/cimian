// BrandingService.cs - Loads custom client branding from client_resources directory.
// Reads branding.yaml for app title and loads sidebar_header.png if present.

using System.IO;
using Microsoft.UI.Xaml.Media.Imaging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

public sealed class BrandingService : IBrandingService
{
    private static readonly string BrandingDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ManagedInstalls", "client_resources");

    private static readonly string BrandingYamlPath = Path.Combine(BrandingDirectory, "branding.yaml");

    private readonly IDeserializer _deserializer;

    public string? AppTitle { get; private set; }
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
        try
        {
            // Load branding.yaml if it exists
            if (File.Exists(BrandingYamlPath))
            {
                var content = await File.ReadAllTextAsync(BrandingYamlPath);
                var branding = _deserializer.Deserialize<BrandingConfig>(content);
                if (branding != null)
                {
                    AppTitle = branding.AppTitle;
                }
            }

            // Load sidebar header image if present
            var headerImage = await TryLoadImageAsync("sidebar_header.png")
                           ?? await TryLoadImageAsync("sidebar_header.jpg");
            SidebarHeaderImage = headerImage;
        }
        catch
        {
            // Use defaults if branding can't be loaded
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
    }
}
