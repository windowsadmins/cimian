namespace Cimian.Infrastructure.System;

/// <summary>
/// Driver-independent display adapter identity.
///
/// Windows only reports a GPU's model name while its vendor driver is bound. The PCI
/// hardware ID is supplied by the bus enumerator instead, so it survives a missing,
/// broken or uninstalled driver - which is exactly when Cimian needs to work out that
/// a driver package should be reinstalled.
/// </summary>
public static class GpuIdentity
{
    /// <summary>
    /// Placeholder names Windows supplies when no vendor driver is bound. They identify a
    /// slot, not a model, so they must never be all a driver predicate has to match on.
    /// </summary>
    private static readonly string[] GenericAdapterNames =
    {
        "microsoft basic display adapter",
        "microsoft basic render driver",
        "microsoft remote display adapter",
        "microsoft hyper-v video",
        "standard vga graphics adapter",
        "vga compatible controller",
        "video controller",
        "display controller",
        "graphics controller"
    };

    /// <summary>PCI vendor IDs for everything that ships a GPU we deploy drivers for.</summary>
    private static readonly Dictionary<string, string> PciVendorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["10DE"] = "NVIDIA",
        ["1002"] = "AMD",
        ["1022"] = "AMD",
        ["8086"] = "Intel",
        ["5143"] = "Qualcomm",
        ["1414"] = "Microsoft",
        ["15AD"] = "VMware",
        ["1AF4"] = "Red Hat"
    };

    /// <summary>
    /// True when a name came from Windows' fallback rather than a vendor driver, and so
    /// tells us nothing about which driver package the card needs.
    /// </summary>
    public static bool IsGenericAdapterName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;

        var lower = name.ToLowerInvariant();
        return GenericAdapterNames.Any(generic => lower.Contains(generic));
    }

    /// <summary>
    /// Reduces a full PNP instance ID to the stable hardware identity, dropping the
    /// subsystem, revision and instance suffixes:
    /// <c>PCI\VEN_10DE&amp;DEV_24B0&amp;SUBSYS_14AD10DE&amp;REV_A1\4&amp;2C4C8C34&amp;0&amp;0008</c>
    /// becomes <c>PCI\VEN_10DE&amp;DEV_24B0</c>. Returns an empty string for anything that
    /// is not a PCI device with both IDs present.
    /// </summary>
    public static string NormalizePciId(string? pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId)) return "";

        var id = pnpDeviceId.Trim();
        if (!id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) return "";

        var vendor = ExtractHexToken(id, "VEN_");
        var device = ExtractHexToken(id, "DEV_");
        if (vendor.Length == 0 || device.Length == 0) return "";

        return $"PCI\\VEN_{vendor}&DEV_{device}";
    }

    /// <summary>
    /// Resolves the vendor name from a PCI ID, e.g. "PCI\VEN_10DE&amp;DEV_24B0" to "NVIDIA".
    /// Returns an empty string for vendors we do not deploy drivers for.
    /// </summary>
    public static string VendorFromPciId(string? pciId)
    {
        if (string.IsNullOrWhiteSpace(pciId)) return "";

        var vendor = ExtractHexToken(pciId, "VEN_");
        return vendor.Length > 0 && PciVendorNames.TryGetValue(vendor, out var name) ? name : "";
    }

    /// <summary>
    /// Reads the hex digits following <paramref name="prefix"/>. Hardware IDs are fixed
    /// width in practice, but scanning to the first non-hex character avoids depending on
    /// that and on the separator that follows.
    /// </summary>
    private static string ExtractHexToken(string id, string prefix)
    {
        var index = id.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return "";

        var start = index + prefix.Length;
        var end = start;
        while (end < id.Length && Uri.IsHexDigit(id[end])) end++;

        return id.Substring(start, end - start).ToUpperInvariant();
    }
}
