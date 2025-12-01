namespace CimianTools.CimiTrigger.Models;

/// <summary>
/// Diagnostic result containing check status and issues found.
/// </summary>
public class DiagnosticResult
{
    /// <summary>
    /// Whether running with admin privileges.
    /// </summary>
    public bool IsAdmin { get; set; }
    
    /// <summary>
    /// Whether the CimianWatcher service is running.
    /// </summary>
    public bool ServiceRunning { get; set; }
    
    /// <summary>
    /// Whether required directories are accessible.
    /// </summary>
    public bool DirectoryOK { get; set; }
    
    /// <summary>
    /// Whether required executables are found.
    /// </summary>
    public bool ExecutablesOK { get; set; }
    
    /// <summary>
    /// List of issues found during diagnostics.
    /// </summary>
    public List<string> Issues { get; set; } = [];
}

/// <summary>
/// Result of an elevation attempt.
/// </summary>
public class ElevationResult
{
    /// <summary>
    /// Whether the elevation was successful.
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// Error message if elevation failed.
    /// </summary>
    public string? Error { get; set; }
    
    /// <summary>
    /// The method that was used (or attempted).
    /// </summary>
    public string? Method { get; set; }
}

/// <summary>
/// Update trigger modes.
/// </summary>
public enum TriggerMode
{
    /// <summary>
    /// GUI mode - shows CimianStatus window.
    /// </summary>
    Gui,
    
    /// <summary>
    /// Headless mode - no GUI.
    /// </summary>
    Headless
}
