using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Service for retrieving authentication credentials from the registry
/// Uses DPAPI to decrypt stored credentials (matching Go pkg/auth implementation)
/// </summary>
public static class AuthService
{
    private const string RegistryPath = @"SOFTWARE\Cimian";
    private const string AuthHeaderName = "AuthHeader";

    /// <summary>
    /// Retrieves and decrypts the AuthHeader from the registry
    /// </summary>
    /// <returns>The decrypted auth header, or null if not found</returns>
    public static string? GetAuthHeader()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath, false);
            if (key == null)
            {
                return null;
            }

            var encryptedValue = key.GetValue(AuthHeaderName) as string;
            if (string.IsNullOrEmpty(encryptedValue))
            {
                return null;
            }

            // Decode from Base64
            var encryptedBytes = Convert.FromBase64String(encryptedValue);

            // Decrypt using DPAPI
            var decryptedBytes = ProtectedData.Unprotect(
                encryptedBytes,
                null,
                DataProtectionScope.LocalMachine);

            var header = System.Text.Encoding.UTF8.GetString(decryptedBytes);

            // Clean up the header
            header = CleanAuthHeader(header);

            return string.IsNullOrEmpty(header) ? null : header;
        }
        catch (Exception)
        {
            // Auth header not available or decryption failed
            return null;
        }
    }

    private static string CleanAuthHeader(string header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return string.Empty;
        }

        // Remove null characters
        header = header.Replace("\0", "");
        
        // Remove "Basic " prefix if present (we add it ourselves)
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            header = header[6..];
        }

        // Remove carriage return and line feed
        header = header.Replace("\r", "").Replace("\n", "");

        // Trim whitespace
        return header.Trim();
    }
}
