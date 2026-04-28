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
    public static readonly string ManifestsDir   = Path.Combine(ManagedInstallsRoot, "manifests");
    public static readonly string LogsDir        = Path.Combine(ManagedInstallsRoot, "logs");
    public static readonly string ReportsDir     = Path.Combine(ManagedInstallsRoot, "reports");
    public static readonly string ConditionsDir  = Path.Combine(ManagedInstallsRoot, "conditions");
    public static readonly string ReceiptsDir    = Path.Combine(ManagedInstallsRoot, "Receipts");
    public static readonly string SbinDir        = Path.Combine(ManagedInstallsRoot, "sbin");
    public static readonly string SelfUpdateBackupDir = Path.Combine(ManagedInstallsRoot, "SelfUpdateBackup");

    // ── Script hooks (sbin) ──────────────────────────────────────────────────
    public static readonly string PreflightScript  = Path.Combine(SbinDir, "preflight.ps1");
    public static readonly string PostflightScript = Path.Combine(SbinDir, "postflight.ps1");

    // ── Bootstrap / coordination flag files ──────────────────────────────────
    public static readonly string BootstrapFlagFile  = Path.Combine(ManagedInstallsRoot, ".cimian.bootstrap");
    public static readonly string HeadlessFlagFile   = Path.Combine(ManagedInstallsRoot, ".cimian.headless");
    public static readonly string SelfUpdateFlagFile = Path.Combine(ManagedInstallsRoot, ".cimian.selfupdate");

    // ── Specific log files ───────────────────────────────────────────────────
    public static readonly string CimiwatcherLog = Path.Combine(LogsDir, "cimiwatcher.log");

    // ── Installed Cimian binaries / scripts (under %ProgramFiles%\Cimian) ────
    public static readonly string ManagedSoftwareUpdateExe = Path.Combine(CimianInstallDir, "managedsoftwareupdate.exe");
    public static readonly string MakeCatalogsExe          = Path.Combine(CimianInstallDir, "makecatalogs.exe");
    public static readonly string CimiStatusExe            = Path.Combine(CimianInstallDir, "cimistatus.exe");
    public static readonly string PreflightScriptInstall   = Path.Combine(CimianInstallDir, "preflight.ps1");
    public static readonly string PostflightScriptInstall  = Path.Combine(CimianInstallDir, "postflight.ps1");
}
