using System.Linq;

namespace Cimian.Core;

/// <summary>
/// Canonical Cimian filesystem locations. All system paths used across CLI tools
/// resolve here so the layout is defined exactly once.
///
/// Roots are computed from environment variables (ProgramData, ProgramFiles) so
/// the binaries don't bake in a drive letter assumption — Windows can relocate
/// these under group policy.
/// </summary>
public static class CimianPaths
{
    /// <summary>%ProgramData%\ManagedInstalls — Cimian's system data root.</summary>
    public static readonly string ManagedInstallsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ManagedInstalls");

    /// <summary>%ProgramFiles%\Cimian — Cimian's binary install root.</summary>
    public static readonly string CimianInstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Cimian");

    // ── System config / state ────────────────────────────────────────────────
    public static readonly string ConfigYaml             = Path.Combine(ManagedInstallsRoot, "Config.yaml");
    public static readonly string SelfServeManifestYaml  = Path.Combine(ManagedInstallsRoot, "SelfServeManifest.yaml");
    public static readonly string InstallInfoYaml        = Path.Combine(ManagedInstallsRoot, "InstallInfo.yaml");

    // ── Subdirectories under ManagedInstallsRoot ─────────────────────────────
    public static readonly string CacheDir       = Path.Combine(ManagedInstallsRoot, "Cache");
    public static readonly string CatalogsDir    = Path.Combine(ManagedInstallsRoot, "catalogs");
    public static readonly string IconsDir       = Path.Combine(ManagedInstallsRoot, "icons");
    public static readonly string ManifestsDir   = Path.Combine(ManagedInstallsRoot, "manifests");
    public static readonly string LogsDir        = Path.Combine(ManagedInstallsRoot, "logs");
    public static readonly string ReportsDir     = Path.Combine(ManagedInstallsRoot, "reports");
    public static readonly string ConditionsDir  = Path.Combine(ManagedInstallsRoot, "conditions");
    public static readonly string ReceiptsDir    = Path.Combine(ManagedInstallsRoot, "Receipts");
    public static readonly string SbinDir        = Path.Combine(ManagedInstallsRoot, "sbin");
    public static readonly string FactsDir       = Path.Combine(ManagedInstallsRoot, "facts");
    public static readonly string SelfUpdateBackupDir = Path.Combine(ManagedInstallsRoot, "SelfUpdateBackup");

    // ── Persisted hardware facts ─────────────────────────────────────────────
    /// <summary>
    /// Last known display adapter identity, keyed by PCI hardware ID. Windows only
    /// reports a GPU's model name while its vendor driver is bound, so this cache is
    /// what lets driver predicates keep matching after a driver goes missing.
    /// </summary>
    public static readonly string GpuFactsCache = Path.Combine(FactsDir, "gpu-adapters.json");

    // ── Script hooks (sbin) ──────────────────────────────────────────────────
    public static readonly string PreflightScript  = Path.Combine(SbinDir, "preflight.ps1");
    public static readonly string PostflightScript = Path.Combine(SbinDir, "postflight.ps1");

    // ── Bootstrap / coordination flag files ──────────────────────────────────
    public static readonly string BootstrapFlagFile  = Path.Combine(ManagedInstallsRoot, ".cimian.bootstrap");
    public static readonly string HeadlessFlagFile   = Path.Combine(ManagedInstallsRoot, ".cimian.headless");
    public static readonly string SelfUpdateFlagFile = Path.Combine(ManagedInstallsRoot, ".cimian.selfupdate");

    // ── Log subdirectories ───────────────────────────────────────────────────
    // The logs root holds the dated session tree (logs\YYYY-MM-DD\HHMM\) and nothing
    // else. Anything written outside a session goes in a named subdirectory here, so
    // the root stays readable and retention has a directory to sweep rather than a
    // pile of loose files it has to pattern-match.

    /// <summary>Verbose installer logs from client self-updates.</summary>
    public static readonly string SelfUpdateLogsDir = Path.Combine(LogsDir, "selfupdate");

    /// <summary>
    /// Verbose msiexec / MSIX logs for managed installs, kept across sessions so a
    /// failed attempt still has its predecessor to compare against. These used to sit in
    /// the download cache, where nothing expired them and they were mixed in with the
    /// payloads.
    /// </summary>
    public static readonly string InstallLogsDir = Path.Combine(LogsDir, "installs");

    /// <summary>
    /// Per-package script output, one directory per package. Written by the packaging
    /// tool's MSI custom actions rather than by this client; named here so retention
    /// knows where to look.
    /// </summary>
    public static readonly string PackageLogsDir = Path.Combine(LogsDir, "packages");


    // ── Specific log files ───────────────────────────────────────────────────
    public static readonly string CimiwatcherLog = Path.Combine(LogsDir, "cimiwatcher.log");

    // ── Installed Cimian binaries / scripts (under %ProgramFiles%\Cimian) ────
    public static readonly string ManagedSoftwareUpdateExe = Path.Combine(CimianInstallDir, "managedsoftwareupdate.exe");
    public static readonly string MakeCatalogsExe          = Path.Combine(CimianInstallDir, "makecatalogs.exe");
    public static readonly string CimiStatusExe            = Path.Combine(CimianInstallDir, "cimistatus.exe");
    public static readonly string PreflightScriptInstall   = Path.Combine(CimianInstallDir, "preflight.ps1");
    public static readonly string PostflightScriptInstall  = Path.Combine(CimianInstallDir, "postflight.ps1");

    /// <summary>
    /// Directories under <see cref="ManagedInstallsRoot"/> whose names are part of the
    /// cross-platform convention and must be exactly lowercase on disk.
    /// </summary>
    public static readonly string[] ConventionDirs =
    {
        LogsDir, ReportsDir, CatalogsDir, IconsDir, ManifestsDir, ConditionsDir, FactsDir
    };

    /// <summary>
    /// Renames a directory that exists with the wrong casing to the name this class
    /// defines, and returns true when it moved one.
    /// </summary>
    /// <remarks>
    /// Changing the path string in this class did not rename anything already on disk:
    /// NTFS is case-insensitive, so a machine that had <c>ManagedInstalls\Logs</c> before the
    /// convention landed keeps that name for ever and reports it upward, while every path
    /// built here happily resolves onto it. Nothing is broken on the endpoint — both spellings
    /// are the same directory — but the reported path is wrong, and a case-sensitive reader
    /// (a log shipper, a mirror on another filesystem) sees two names for one thing.
    ///
    /// The rename goes via a temporary name because a direct case-only Move is a no-op on a
    /// case-insensitive volume. Everything is best effort: this runs at the start of every
    /// session, and a directory that cannot be renamed — held open by another process, denied,
    /// or already correct — must never stop a run.
    /// </remarks>
    public static bool NormalizeDirectoryCasing(string desiredPath)
    {
        try
        {
            var parent = Path.GetDirectoryName(desiredPath);
            var wanted = Path.GetFileName(desiredPath);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(wanted) || !Directory.Exists(parent))
                return false;

            var matches = Directory.GetDirectories(parent)
                .Where(d => string.Equals(Path.GetFileName(d), wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Exactly one entry is the normal case. Zero means nothing to do; more than one
            // means a case-sensitive volume holds both spellings as separate directories, and
            // merging them is a data decision this must not take on its own.
            if (matches.Count != 1) return false;

            var actual = Path.GetFileName(matches[0]);
            if (string.Equals(actual, wanted, StringComparison.Ordinal)) return false;

            var staging = Path.Combine(parent, $"{wanted}.casing-{Guid.NewGuid():N}");
            Directory.Move(matches[0], staging);
            Directory.Move(staging, desiredPath);
            return true;
        }
        catch
        {
            // Cosmetic correction; never worth failing a run over.
            return false;
        }
    }
}
