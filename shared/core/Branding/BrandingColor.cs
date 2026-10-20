using System.Globalization;

namespace Cimian.Core.Branding;

/// <summary>
/// An opaque sRGB colour from <c>accent_color</c> or the <c>AccentColor</c> policy, with
/// the lighter and darker shades Windows derives from an accent.
/// </summary>
public readonly record struct BrandingColor(byte R, byte G, byte B)
{
    /// <summary>
    /// Parses <c>#RGB</c>, <c>#RRGGBB</c> or <c>#AARRGGBB</c>, with or without the leading
    /// <c>#</c>. Alpha is dropped: an accent has to be opaque to stay readable.
    /// </summary>
    public static bool TryParse(string? text, out BrandingColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hex = text.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];

        if (hex.Length == 3)
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        else if (hex.Length == 8)
            hex = hex[2..];

        if (hex.Length != 6 ||
            !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return false;

        color = new BrandingColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    /// <summary>The colour as <c>#RRGGBB</c>.</summary>
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>Mixes this colour toward <paramref name="other"/> by <paramref name="amount"/> (0–1).</summary>
    public BrandingColor Mix(BrandingColor other, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte Blend(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return new BrandingColor(Blend(R, other.R), Blend(G, other.G), Blend(B, other.B));
    }

    public static readonly BrandingColor White = new(255, 255, 255);
    public static readonly BrandingColor Black = new(0, 0, 0);

    /// <summary>
    /// The three lighter shades WinUI reads as <c>SystemAccentColorLight1..3</c>; dark theme
    /// fills accent buttons with Light2.
    /// </summary>
    public BrandingColor[] LightShades() =>
        [Mix(White, 0.25), Mix(White, 0.45), Mix(White, 0.65)];

    /// <summary>
    /// The three darker shades WinUI reads as <c>SystemAccentColorDark1..3</c>; light theme
    /// fills accent buttons with Dark1.
    /// </summary>
    public BrandingColor[] DarkShades() =>
        [Mix(Black, 0.2), Mix(Black, 0.4), Mix(Black, 0.6)];

    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    public double RelativeLuminance()
    {
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
    }

    /// <summary>WCAG contrast ratio between two colours, 1 to 21.</summary>
    public static double ContrastRatio(BrandingColor a, BrandingColor b)
    {
        var la = a.RelativeLuminance();
        var lb = b.RelativeLuminance();
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>White or black, whichever reads better on this colour.</summary>
    public BrandingColor ReadableForeground() =>
        ContrastRatio(this, White) >= ContrastRatio(this, Black) ? White : Black;
}
