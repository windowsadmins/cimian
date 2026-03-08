// Converters.cs - XAML Value Converters (WinUI 3)

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

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
