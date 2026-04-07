using YamlDotNet.Serialization;

namespace Cimian.Msi.Models;

/// <summary>
/// Installation receipt stored at %ProgramData%\ManagedInstalls\Receipts\{identifier}.yaml.
/// Provides a reliable, Cimian-controlled record of what was installed, supplementing
/// the Windows Installer registry which can be inconsistent.
/// </summary>
public class MsiReceipt
{
    [YamlMember(Alias = "product")]
    public MsiReceiptProduct Product { get; set; } = new();

    [YamlMember(Alias = "msi")]
    public MsiReceiptInstaller Msi { get; set; } = new();

    [YamlMember(Alias = "install")]
    public MsiReceiptInstall Install { get; set; } = new();
}

public class MsiReceiptProduct
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "version")]
    public string Version { get; set; } = string.Empty;

    [YamlMember(Alias = "identifier")]
    public string Identifier { get; set; } = string.Empty;

    [YamlMember(Alias = "developer")]
    public string Developer { get; set; } = string.Empty;
}

public class MsiReceiptInstaller
{
    [YamlMember(Alias = "product_code")]
    public string ProductCode { get; set; } = string.Empty;

    [YamlMember(Alias = "upgrade_code")]
    public string UpgradeCode { get; set; } = string.Empty;

    [YamlMember(Alias = "msi_version")]
    public string MsiVersion { get; set; } = string.Empty;

    [YamlMember(Alias = "full_version")]
    public string FullVersion { get; set; } = string.Empty;
}

public class MsiReceiptInstall
{
    [YamlMember(Alias = "date")]
    public string Date { get; set; } = string.Empty;

    [YamlMember(Alias = "method")]
    public string Method { get; set; } = "msi-native";

    [YamlMember(Alias = "install_location")]
    public string? InstallLocation { get; set; }
}
