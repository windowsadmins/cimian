namespace Cimian.Msi.Models;

/// <summary>
/// Metadata extracted from or written to an MSI database Property table.
/// </summary>
public class MsiMetadata
{
    public string ProductName { get; set; } = string.Empty;
    public string ProductVersion { get; set; } = string.Empty;
    public string FullVersion { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string UpgradeCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
    public string? InstallLocation { get; set; }
    public string? Architecture { get; set; }
    public string? BuildInfoYaml { get; set; }

    /// <summary>
    /// Whether this MSI was built by cimipkg (has CIMIAN_BUILD_INFO property).
    /// </summary>
    public bool IsCimianPackage => !string.IsNullOrEmpty(BuildInfoYaml);

    /// <summary>
    /// Additional custom properties to embed in the MSI.
    /// </summary>
    public Dictionary<string, string> CustomProperties { get; set; } = [];
}
