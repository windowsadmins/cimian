using System.Collections;
using WixToolset.Dtf.WindowsInstaller;

namespace Cimian.CLI.Cimiimport.Services;

/// <summary>
/// One installed file row from the MSI's native bill of materials
/// (the File ⨯ Component ⨯ Directory tables).
/// </summary>
public sealed record MsiInstalledFile(
    string AbsolutePath,
    long FileSize,
    string? Version,
    bool IsKeyPath);

/// <summary>
/// Queries an MSI's native bill of materials to enumerate the files it would
/// install, with each file's absolute target path resolved by walking the
/// Directory table to TARGETDIR.
///
/// Every cimipkg- and WiX-built MSI already populates these tables — there's
/// no need for a sidecar manifest. Used by cimiimport to auto-populate the
/// pkginfo "key_path" field and to seed top-N file checks for third-party
/// MSIs whose payload is too large to enumerate exhaustively.
/// </summary>
public static class MsiBomReader
{
    /// <summary>
    /// Standard Windows Installer system folder identifiers. Resolved to their
    /// canonical absolute paths. Anything not in this map is treated as a
    /// custom directory and resolved via Directory.DefaultDir.
    /// See https://learn.microsoft.com/en-us/windows/win32/msi/property-reference#system-folder-properties
    /// </summary>
    private static readonly Dictionary<string, string> WellKnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TARGETDIR"]            = @"C:\",
        ["ProgramFiles64Folder"] = @"C:\Program Files",
        ["ProgramFilesFolder"]   = @"C:\Program Files (x86)",
        ["CommonFiles64Folder"]  = @"C:\Program Files\Common Files",
        ["CommonFilesFolder"]    = @"C:\Program Files (x86)\Common Files",
        ["CommonAppDataFolder"]  = @"C:\ProgramData",
        ["WindowsFolder"]        = @"C:\Windows",
        ["SystemFolder"]         = @"C:\Windows\System32",
        ["System64Folder"]       = @"C:\Windows\System32",
        ["FontsFolder"]          = @"C:\Windows\Fonts",
        ["TempFolder"]           = @"C:\Windows\Temp",
        ["DesktopFolder"]        = @"C:\Users\Public\Desktop",
        ["ProgramMenuFolder"]    = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs",
    };

    /// <summary>
    /// Enumerates files installed by the MSI whose extension matches one of
    /// <paramref name="extensions"/> (default: <c>.exe</c>). Returns absolute
    /// target paths sorted by FileSize descending — callers can take the top
    /// N or apply name-match heuristics on top of the ordered list.
    /// </summary>
    public static List<MsiInstalledFile> EnumerateInstalledFiles(
        Database db,
        string[]? extensions = null)
    {
        var exts = extensions ?? [".exe"];
        var result = new List<MsiInstalledFile>();

        try
        {
            // Build the Directory → absolute path lookup once for the whole pass.
            var dirPaths = BuildDirectoryPathMap(db);

            // Join File + Component to get keypath designation per file.
            // Component.KeyPath = File.File means this file is the keypath of
            // its component (every cimipkg-built component has its single file
            // as its own keypath, but third-party MSIs use this distinction).
            using var view = db.OpenView(
                "SELECT `File`.`File`, `File`.`FileName`, `File`.`FileSize`, `File`.`Version`, " +
                "`Component`.`Directory_`, `Component`.`KeyPath` " +
                "FROM `File`, `Component` WHERE `File`.`Component_` = `Component`.`Component`");
            view.Execute();

            for (var record = view.Fetch(); record != null; record = view.Fetch())
            {
                using (record)
                {
                    var fileKey = record.GetString(1);
                    var fileName = record.GetString(2);
                    var fileSize = record.GetInteger(3);
                    var version = record.GetString(4);
                    var directoryKey = record.GetString(5);
                    var componentKeyPath = record.GetString(6);

                    var targetName = ExtractLongTargetName(fileName);
                    if (string.IsNullOrEmpty(targetName)) continue;

                    if (!exts.Any(e => targetName.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    if (!dirPaths.TryGetValue(directoryKey, out var dirPath))
                        continue;

                    // FileSize column type is INTEGER, returned as long; the
                    // DTF .GetInteger() call above narrows to int, fine for
                    // files up to 2 GB. For larger files the comparison still
                    // works monotonically (largest wins) even if individual
                    // sizes saturate.
                    result.Add(new MsiInstalledFile(
                        AbsolutePath: NormalizePath(Path.Combine(dirPath, targetName)),
                        FileSize: fileSize,
                        Version: string.IsNullOrEmpty(version) ? null : version,
                        IsKeyPath: string.Equals(fileKey, componentKeyPath, StringComparison.Ordinal)));
                }
            }
        }
        catch
        {
            // MSI table query failed (older MSI format, missing tables, etc.).
            // Return whatever we collected — caller treats empty as "no auto
            // key_path", same as if the MSI had no .exe files.
        }

        return result
            .OrderByDescending(f => f.FileSize)
            .ToList();
    }

    /// <summary>
    /// Picks the best key_path candidate from an ordered (largest-first) file
    /// list. Returns null when no candidate exists.
    /// 1. Single .exe → it.
    /// 2. .exe whose stem matches <paramref name="productName"/> (case-insensitive) → it.
    /// 3. Largest .exe → it.
    /// </summary>
    public static string? PickPrimaryBinary(List<MsiInstalledFile> orderedFiles, string productName)
    {
        if (orderedFiles.Count == 0) return null;
        if (orderedFiles.Count == 1) return orderedFiles[0].AbsolutePath;

        if (!string.IsNullOrEmpty(productName))
        {
            var match = orderedFiles.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f.AbsolutePath)
                    .Equals(productName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match.AbsolutePath;
        }

        return orderedFiles[0].AbsolutePath;
    }

    /// <summary>
    /// Walks the MSI Directory table from every leaf upward to TARGETDIR,
    /// returning a map of Directory key → resolved absolute path. Well-known
    /// system folders are substituted with their canonical paths; everything
    /// else uses DefaultDir's long-name component.
    /// </summary>
    private static Dictionary<string, string> BuildDirectoryPathMap(Database db)
    {
        var directories = new Dictionary<string, (string Parent, string DefaultDir)>(StringComparer.Ordinal);

        using (var view = db.OpenView("SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`"))
        {
            view.Execute();
            for (var record = view.Fetch(); record != null; record = view.Fetch())
            {
                using (record)
                {
                    directories[record.GetString(1)] = (record.GetString(2) ?? "", record.GetString(3) ?? "");
                }
            }
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var key in directories.Keys)
        {
            ResolvePath(key, directories, resolved);
        }

        return resolved;
    }

    private static string ResolvePath(
        string directoryKey,
        Dictionary<string, (string Parent, string DefaultDir)> directories,
        Dictionary<string, string> resolved)
    {
        if (resolved.TryGetValue(directoryKey, out var cached))
            return cached;

        // Well-known system folder — use canonical absolute path.
        if (WellKnownFolders.TryGetValue(directoryKey, out var known))
        {
            resolved[directoryKey] = known;
            return known;
        }

        if (!directories.TryGetValue(directoryKey, out var entry))
        {
            resolved[directoryKey] = "";
            return "";
        }

        var segment = ExtractLongTargetName(entry.DefaultDir);

        // "." is the MSI shorthand for "use the parent directory directly,
        // no extra segment" — used by cimipkg for INSTALLDIR under TempFolder
        // and by countless WiX patterns.
        var addSegment = !string.IsNullOrEmpty(segment) && segment != ".";

        var parentPath = string.IsNullOrEmpty(entry.Parent)
            ? ""
            : ResolvePath(entry.Parent, directories, resolved);

        var combined = addSegment
            ? (string.IsNullOrEmpty(parentPath) ? segment : Path.Combine(parentPath, segment))
            : parentPath;

        resolved[directoryKey] = combined;
        return combined;
    }

    /// <summary>
    /// MSI File.FileName and Directory.DefaultDir use one of three formats:
    ///   <c>name</c>                                  → both short and long are "name"
    ///   <c>shortName|longName</c>                    → 8.3 short + long
    ///   <c>sourceShortName|sourceLongName:targetShortName|targetLongName</c> (Directory only)
    /// We want the long, target-side name for path resolution.
    /// </summary>
    private static string ExtractLongTargetName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        // Directory.DefaultDir can carry a "source:target" pair — keep target.
        var colonIdx = raw.IndexOf(':');
        var target = colonIdx >= 0 ? raw.Substring(colonIdx + 1) : raw;

        // "short|long" → keep long.
        var pipeIdx = target.IndexOf('|');
        return pipeIdx >= 0 ? target.Substring(pipeIdx + 1) : target;
    }

    private static string NormalizePath(string path)
    {
        // Collapse any forward slashes from FileName (MSI tables sometimes
        // carry mixed separators) and remove trailing whitespace.
        return path.Replace('/', '\\').TrimEnd();
    }
}
