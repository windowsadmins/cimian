// Converters.cs - XAML Value Converters (WinUI 3)

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Cimian.GUI.ManagedSoftwareCenter.Converters;

/// <summary>
/// Converts null/empty to Visibility.Collapsed
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string str)
        {
            return string.IsNullOrWhiteSpace(str) ? Visibility.Collapsed : Visibility.Visible;
        }
        return value == null ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts bool to Visibility (true = Visible)
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b)
        {
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is Visibility v)
        {
            return v == Visibility.Visible;
        }
        return false;
    }
}

/// <summary>
/// Converts bool to Visibility (true = Collapsed, false = Visible)
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b)
        {
            return b ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts file size in bytes to human-readable string
/// </summary>
public class FileSizeConverter : IValueConverter
{
    private static readonly string[] SizeSuffixes = { "B", "KB", "MB", "GB", "TB" };

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        long bytes = 0;
        
        if (value is long l) bytes = l;
        else if (value is int i) bytes = i;
        else if (value is double d) bytes = (long)d;
        else return "Unknown";

        if (bytes <= 0) return "0 B";

        int mag = (int)Math.Log(bytes, 1024);
        mag = Math.Min(mag, SizeSuffixes.Length - 1);
        
        double adjustedSize = bytes / Math.Pow(1024, mag);

        return $"{adjustedSize:N1} {SizeSuffixes[mag]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts double (0-100) to percentage string
/// </summary>
public class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double d)
        {
            return $"{d:F0}%";
        }
        return "0%";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Formats strings with parameters
/// </summary>
public class StringFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (parameter is string format && value != null)
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, format, value);
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Renders a live item lifecycle stage (pending / downloading / downloaded /
/// installing / installed / removing / removed / failed) as one facet chosen
/// by ConverterParameter:
///   text    — display label
///   brush   — SolidColorBrush for label and glyph
///   glyph   — Segoe Fluent icon for terminal/idle stages
///   spinner — Visibility of the in-progress ring (downloading/installing/removing)
///   icon    — Visibility of the static glyph (everything else)
///   panel   — Visibility of the whole stage panel (collapsed when no stage)
///   bar     — Visibility of the slim in-row progress bar (active stages only)
/// </summary>
public class ItemStageConverter : IValueConverter
{
    // Cached once: the "brush" facet is hit on every layout/scroll pass, so
    // allocating a fresh SolidColorBrush per Convert call was needless GC churn.
    private static readonly SolidColorBrush GreenBrush = new(Windows.UI.Color.FromArgb(255, 16, 124, 16));
    private static readonly SolidColorBrush RedBrush = new(Windows.UI.Color.FromArgb(255, 209, 52, 56));
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(255, 0, 120, 212));
    private static readonly SolidColorBrush GrayBrush = new(Windows.UI.Color.FromArgb(255, 110, 110, 110));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var stage = (value as string)?.ToLowerInvariant();
        var facet = parameter as string ?? "text";
        var active = stage is "downloading" or "installing" or "removing";

        return facet switch
        {
            "panel" => string.IsNullOrEmpty(stage) ? Visibility.Collapsed : Visibility.Visible,
            "spinner" => active ? Visibility.Visible : Visibility.Collapsed,
            // Slim per-row progress bar: shown while the item is actively worked
            // (download/install/remove) so a targeted --item run renders progress
            // in the row itself instead of the global banner.
            "bar" => active ? Visibility.Visible : Visibility.Collapsed,
            "icon" => !active && !string.IsNullOrEmpty(stage) ? Visibility.Visible : Visibility.Collapsed,
            "text" => stage switch
            {
                "pending" => "Pending",
                "downloading" => "Downloading...",
                "downloaded" => "Downloaded",
                "installing" => "Installing...",
                "installed" => "Installed",
                "removing" => "Removing...",
                "removed" => "Removed",
                "failed" => "Failed",
                _ => string.Empty
            },
            "glyph" => stage switch
            {
                "pending" => "",                // Recent (clock)
                "downloaded" => "",             // Download
                "installed" or "removed" => "", // CheckMark
                "failed" => "",                 // Cancel (X)
                _ => string.Empty
            },
            "brush" => stage switch
            {
                "installed" or "removed" => GreenBrush,
                "failed" => RedBrush,
                "downloading" or "installing" or "removing" => AccentBrush,
                "downloaded" => GreenBrush,
                _ => GrayBrush                                                             // pending
            },
            _ => string.Empty
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Inverse of ItemStageConverter's "panel" facet: visible only when the item
/// has NO live stage (idle), used for the static "Will be installed" labels.
/// </summary>
public class NoStageToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts install status to display color/brush
/// </summary>
public class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string status)
        {
            return status.ToLowerInvariant() switch
            {
                "installed" => "#107C10", // Green
                "update-available" => "#0078D4", // Blue
                "will-be-installed" or "pending" or "requested" => "#FFB900", // Yellow/Orange
                "will-be-removed" => "#D13438", // Red
                "not-installed" => "#6E6E6E", // Gray
                _ => "#6E6E6E"
            };
        }
        return "#6E6E6E";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
