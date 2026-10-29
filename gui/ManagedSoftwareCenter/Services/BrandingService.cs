// BrandingService.cs - Client branding for Managed Software Center.
// Resolution (policy > branding.yaml > defaults) lives in Cimian.Core.Branding so
// CimianStatus brands the same way and the rules are unit-tested.

using System.IO;
using Cimian.Core.Branding;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

public sealed class BrandingService : IBrandingService
{
    public MscBranding Current { get; private set; } = MscBranding.Default;

    public string? AppTitle => Current.AppTitle;
    public string ResolvedBannerTitle => Current.BannerTitle;
    public bool ShowBannerTitle => Current.ShowBannerTitle;
    public BitmapImage? SidebarHeaderImage { get; private set; }
    public BitmapImage? LogoImage { get; private set; }

    public BrandingService()
    {
        Reload();
    }

    public MscBranding Reload()
    {
        Current = MscBrandingResolver.Load();
        foreach (var warning in Current.Warnings)
            System.Diagnostics.Debug.WriteLine($"[Branding] {warning}");
        return Current;
    }

    public async Task LoadAsync()
    {
        Reload();
        SidebarHeaderImage = await LoadImageAsync(Current.SidebarHeaderPath);
        LogoImage = await LoadImageAsync(Current.LogoPath);
    }

    public async Task<BitmapImage?> LoadImageAsync(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        try
        {
            // Copy to memory first so the file is not held open while the app runs;
            // managedsoftwareupdate replaces these files when the zip changes.
            var memStream = new MemoryStream(await File.ReadAllBytesAsync(path));
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(memStream.AsRandomAccessStream());
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
