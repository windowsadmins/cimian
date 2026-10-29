// BrandingTheme.cs - Applies a branded accent colour to the WinUI resource system.

using Cimian.Core.Branding;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

public static class BrandingTheme
{
    /// <summary>
    /// The accent in use: the branded one, or the Windows accent when none is set.
    /// </summary>
    public static Color AccentColor =>
        Application.Current?.Resources.TryGetValue("SystemAccentColor", out var value) == true && value is Color c
            ? c
            : Color.FromArgb(255, 0, 120, 212);

    /// <summary>
    /// Overrides the Windows accent for this app. WinUI derives every accent fill, link and
    /// focus colour from SystemAccentColor and its six shades, so replacing those seven keys
    /// at app level rebrands the stock controls. Must run before the first window is created:
    /// controls resolve these resources when they load.
    /// </summary>
    public static void ApplyAccent(ResourceDictionary resources, BrandingColor? accent)
    {
        if (accent is not { } a)
            return;

        resources["SystemAccentColor"] = ToColor(a);

        var light = a.LightShades();
        var dark = a.DarkShades();
        for (var i = 0; i < 3; i++)
        {
            resources[$"SystemAccentColorLight{i + 1}"] = ToColor(light[i]);
            resources[$"SystemAccentColorDark{i + 1}"] = ToColor(dark[i]);
        }

        // Accent buttons are filled with Dark1 in light theme and Light2 in dark theme.
        // Pick the text colour that reads on each, so a pale brand colour still works.
        SetThemeBrush(resources, "Light", "TextOnAccentFillColorPrimaryBrush", dark[0].ReadableForeground());
        SetThemeBrush(resources, "Dark", "TextOnAccentFillColorPrimaryBrush", light[1].ReadableForeground());
        SetThemeBrush(resources, "Default", "TextOnAccentFillColorPrimaryBrush", light[1].ReadableForeground());
    }

    /// <summary>A diagonal gradient from the accent to its darkest shade, for the hero banner.</summary>
    public static Brush? HeroGradient(BrandingColor? accent)
    {
        if (accent is not { } a)
            return null;

        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop { Color = ToColor(a), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = ToColor(a.DarkShades()[2]), Offset = 1 });
        return brush;
    }

    /// <summary>A lighter accent gradient for the Featured tile beside the hero banner.</summary>
    public static Brush? TileGradient(BrandingColor? accent)
    {
        if (accent is not { } a)
            return null;

        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop { Color = ToColor(a.LightShades()[0]), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = ToColor(a.DarkShades()[0]), Offset = 1 });
        return brush;
    }

    public static Color ToColor(BrandingColor c) => Color.FromArgb(255, c.R, c.G, c.B);

    private static void SetThemeBrush(ResourceDictionary resources, string theme, string key, BrandingColor color)
    {
        if (!resources.ThemeDictionaries.TryGetValue(theme, out var existing) ||
            existing is not ResourceDictionary dictionary)
        {
            dictionary = new ResourceDictionary();
            resources.ThemeDictionaries[theme] = dictionary;
        }
        dictionary[key] = new SolidColorBrush(ToColor(color));
    }
}
