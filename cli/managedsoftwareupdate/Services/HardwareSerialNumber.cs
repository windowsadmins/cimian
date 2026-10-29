namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>Preserves a usable BIOS serial, falling back to the motherboard serial.</summary>
public static class HardwareSerialNumber
{
    public static string? Resolve(Func<string?> readBios, Func<string?> readBaseBoard)
        => TryRead(readBios) ?? TryRead(readBaseBoard);

    public static string? Normalize(string? value)
    {
        var serial = value?.Trim();
        return string.IsNullOrEmpty(serial) ||
            string.Equals(serial, "Default String", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(serial, "System Serial Number", StringComparison.OrdinalIgnoreCase)
            ? null : serial;
    }

    private static string? TryRead(Func<string?> read)
    {
        try
        {
            return Normalize(read());
        }
        catch
        {
            // Failure of one firmware source must not prevent trying the next.
            return null;
        }
    }
}
