using System.Security.Cryptography;
using System.Text;

namespace Cimian.Msi.Services;

/// <summary>
/// Generates deterministic UpgradeCodes from product identifiers using UUID v5 (SHA-1 namespace).
/// Ensures the same product identifier always produces the same UpgradeCode across builds,
/// while ProductCode changes with each build for proper major upgrade behavior.
/// </summary>
public static class UpgradeCodeGenerator
{
    // Cimian namespace UUID for GUID v5 generation.
    // This is a fixed, arbitrary UUID that acts as the namespace for all Cimian UpgradeCodes.
    private static readonly Guid CimianNamespace = new("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d");

    /// <summary>
    /// Generate a deterministic UpgradeCode from a product identifier.
    /// Uses UUID v5 (RFC 4122) with SHA-1 hashing.
    /// </summary>
    /// <param name="identifier">Product identifier (e.g., "com.autodesk.Maya")</param>
    /// <returns>Deterministic GUID suitable for MSI UpgradeCode</returns>
    public static Guid GenerateUpgradeCode(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return CreateGuidV5(CimianNamespace, identifier);
    }

    /// <summary>
    /// Generate a new random ProductCode. Each build gets a unique ProductCode.
    /// </summary>
    public static Guid GenerateProductCode() => Guid.NewGuid();

    /// <summary>
    /// Generate a deterministic ComponentId from a relative file path and product identifier.
    /// Ensures the same file in the same product always gets the same ComponentId.
    /// </summary>
    public static Guid GenerateComponentId(string identifier, string relativePath)
    {
        var input = $"{identifier}:{relativePath.Replace('\\', '/')}";
        return CreateGuidV5(CimianNamespace, input);
    }

    /// <summary>
    /// Creates a UUID v5 (SHA-1 name-based) per RFC 4122.
    /// </summary>
    private static Guid CreateGuidV5(Guid namespaceId, string name)
    {
        var namespaceBytes = namespaceId.ToByteArray();
        SwapByteOrder(namespaceBytes);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var data = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, data, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, data, namespaceBytes.Length, nameBytes.Length);

        var hash = SHA1.HashData(data);

        // Set version (5) and variant (RFC 4122)
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // Version 5
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // Variant RFC 4122

        var result = new byte[16];
        Array.Copy(hash, 0, result, 0, 16);
        SwapByteOrder(result);

        return new Guid(result);
    }

    /// <summary>
    /// Swap byte order for GUID fields to match RFC 4122 network byte order.
    /// .NET GUIDs store the first three fields in little-endian; RFC 4122 uses big-endian.
    /// </summary>
    private static void SwapByteOrder(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }
}
