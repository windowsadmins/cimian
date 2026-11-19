using System.Security.Cryptography;
using WixToolset.Dtf.WindowsInstaller;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Gives a same-product update the basename recorded by Windows Installer.
/// Versioned downloads remain immutable; staged sources are retained for repair.
/// </summary>
internal static class MsiSourceStaging
{
    internal static string Prepare(string packagePath, string? registeredName)
    {
        if (string.IsNullOrEmpty(registeredName) ||
            string.Equals(Path.GetFileName(packagePath), registeredName, StringComparison.OrdinalIgnoreCase))
            return packagePath;

        ValidatePackageName(registeredName);
        // Relocating an MSI must not separate it from external cabinets or loose
        // files. Reject an unsafe relocation before invoking Windows Installer.
        using (var database = new Database(packagePath, DatabaseOpenMode.ReadOnly))
        {
            if (database.Tables.Contains("Media"))
            {
                using var view = database.OpenView("SELECT `Cabinet` FROM `Media`");
                view.Execute();
                while (view.Fetch() is { } row)
                {
                    using (row)
                    {
                        var cabinet = row.GetString(1);
                        if (!string.IsNullOrEmpty(cabinet) && !cabinet.StartsWith('#'))
                            throw new InvalidOperationException("Cannot stage a renamed MSI with external cabinet sources.");
                    }
                }
            }
            if (database.Tables.Contains("File"))
            {
                var compressedByDefault = (database.SummaryInfo.WordCount & 2) != 0;
                using var view = database.OpenView("SELECT `Attributes` FROM `File`");
                view.Execute();
                while (view.Fetch() is { } row)
                {
                    using (row)
                    {
                        var attributes = row.GetInteger(1);
                        if ((attributes & 0x2000) != 0 || (!compressedByDefault && (attributes & 0x4000) == 0))
                            throw new InvalidOperationException("Cannot stage a renamed MSI with loose-file sources.");
                    }
                }
            }
        }

        return StageVerifiedCopy(packagePath, registeredName);
    }

    internal static string StageVerifiedCopy(string packagePath, string registeredName)
    {
        ValidatePackageName(registeredName);
        packagePath = Path.GetFullPath(packagePath);
        // Keep the source open without write/delete sharing until the verified
        // copy is complete, so the directory identity describes the copied bytes.
        using var source = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = SHA256.HashData(source);
        var root = Path.Combine(Path.GetDirectoryName(packagePath)!, ".msi-sources");
        var directory = Path.Combine(root, Convert.ToHexString(hash));
        EnsureDirectory(root);
        EnsureDirectory(directory);
        var staged = Path.Combine(directory, registeredName);
        if (File.Exists(staged))
        {
            Verify(staged, hash);
            return staged;
        }

        var temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            source.Position = 0;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.CopyTo(output);
            Verify(temporary, hash);
            // Never replace an existing source, including one staged by another
            // process. A concurrent identical copy can be reused after validation.
            try { File.Move(temporary, staged); }
            catch (IOException) when (File.Exists(staged)) { Verify(staged, hash); }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return staged;
    }

    private static void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0)
            throw new IOException("MSI source staging cannot use a linked directory.");
    }

    private static void Verify(string path, byte[] hash)
    {
        if ((File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0)
            throw new IOException("MSI source staging cannot use a linked file.");
        using var stream = File.OpenRead(path);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), hash))
            throw new IOException("Staged MSI source does not match the incoming package.");
    }

    internal static void ValidatePackageName(string name)
    {
        var stem = name.Split('.')[0];
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name != Path.GetFileName(name) || !name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(' ') || stem.EndsWith(' ') ||
            new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                                 "123456789¹²³".Contains(stem[3])))
            throw new InvalidOperationException("Registered MSI PackageName is not a safe MSI basename.");
    }
}
