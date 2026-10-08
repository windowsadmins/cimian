using System.ComponentModel;
using System.Text;

namespace Cimian.Core.Msi;

/// <summary>
/// Queries the products Windows Installer has registered on this machine.
/// </summary>
public static class MsiProducts
{
    /// <summary>
    /// The ProductCodes of installed products that share <paramref name="upgradeCode"/>.
    /// Enumerated lazily; throws <see cref="Win32Exception"/> if the enumeration fails.
    /// </summary>
    public static IEnumerable<string> GetRelatedProducts(string upgradeCode)
    {
        var buffer = new StringBuilder(39);
        for (uint i = 0; ; i++)
        {
            var result = MsiNative.MsiEnumRelatedProductsW(upgradeCode, 0, i, buffer);
            if (result == MsiNative.ERROR_NO_MORE_ITEMS)
            {
                yield break;
            }
            MsiNative.Check(result);
            yield return buffer.ToString();
        }
    }

    /// <summary>
    /// The installed product's VersionString, normalised to a <see cref="System.Version"/>:
    /// anything after the first character that is neither a digit nor a dot is
    /// dropped, a bare number gains ".0", and parts past the fourth are cut.
    /// Null when the product has no readable version.
    /// </summary>
    public static System.Version? GetProductVersion(string productCode) =>
        ParseVersion(GetProductInfo(productCode, "VersionString"));

    /// <summary>An installed product's property, or null when it cannot be read.</summary>
    public static string? GetProductInfo(string productCode, string property)
    {
        uint length = 40;
        var buffer = new StringBuilder((int)length);
        var result = MsiNative.MsiGetProductInfoW(productCode, property, buffer, ref length);
        if (result == MsiNative.ERROR_MORE_DATA)
        {
            length++;
            buffer = new StringBuilder((int)length);
            result = MsiNative.MsiGetProductInfoW(productCode, property, buffer, ref length);
        }
        return result == 0 ? buffer.ToString() : null;
    }

    internal static System.Version? ParseVersion(string? version)
    {
        if (version == null)
        {
            return null;
        }

        var dots = 0;
        for (var i = 0; i < version.Length; i++)
        {
            var c = version[i];
            if (c == '.')
            {
                dots++;
            }
            else if (!char.IsDigit(c))
            {
                version = version[..i];
                break;
            }
        }

        if (version.Length == 0)
        {
            return null;
        }
        if (dots == 0)
        {
            version += ".0";
        }
        else if (dots > 3)
        {
            version = string.Join(".", version.Split('.'), 0, 4);
        }

        try
        {
            return new System.Version(version);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
