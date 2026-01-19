using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Cimian.CLI.Cimiimport.Models;

/// <summary>
/// Package info YAML structure for Cimian.
/// </summary>
public class PkgsInfo
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "display_name")]
    public string? DisplayName { get; set; }

    [YamlMember(Alias = "identifier")]
    public string? Identifier { get; set; }

    [YamlMember(Alias = "version")]
    public string Version { get; set; } = "";

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    [YamlMember(Alias = "category")]
    public string Category { get; set; } = "";

    [YamlMember(Alias = "icon_name")]
    public string? IconName { get; set; }

    [YamlMember(Alias = "developer")]
    public string Developer { get; set; } = "";

    [YamlMember(Alias = "catalogs")]
    public List<string> Catalogs { get; set; } = [];

    [YamlMember(Alias = "installs")]
    public List<InstallItem>? Installs { get; set; }

    [YamlMember(Alias = "supported_architectures")]
    public List<string> SupportedArch { get; set; } = [];

    [YamlMember(Alias = "unattended_install")]
    public bool UnattendedInstall { get; set; }

    [YamlMember(Alias = "unattended_uninstall")]
    public bool UnattendedUninstall { get; set; }

    [YamlMember(Alias = "requires")]
    public List<string>? Requires { get; set; }

    [YamlMember(Alias = "update_for")]
    public List<string>? UpdateFor { get; set; }

    [YamlMember(Alias = "blocking_applications")]
    public List<string>? BlockingApps { get; set; }

    [YamlMember(Alias = "minimum_os_version")]
    public string? MinOSVersion { get; set; }

    [YamlMember(Alias = "maximum_os_version")]
    public string? MaxOSVersion { get; set; }

    [YamlMember(Alias = "installer")]
    public Installer? Installer { get; set; }

    [YamlMember(Alias = "uninstaller")]
    public Installer? Uninstaller { get; set; }

    [YamlMember(Alias = "preinstall_script")]
    public string? PreinstallScript { get; set; }

    [YamlMember(Alias = "postinstall_script")]
    public string? PostinstallScript { get; set; }

    [YamlMember(Alias = "preuninstall_script")]
    public string? PreuninstallScript { get; set; }

    [YamlMember(Alias = "postuninstall_script")]
    public string? PostuninstallScript { get; set; }

    [YamlMember(Alias = "installcheck_script")]
    public string? InstallCheckScript { get; set; }

    [YamlMember(Alias = "uninstallcheck_script")]
    public string? UninstallCheckScript { get; set; }
}

/// <summary>
/// Installer/uninstaller details.
/// </summary>
public class Installer
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "";

    [YamlMember(Alias = "size")]
    public long Size { get; set; }

    [YamlMember(Alias = "location")]
    public string Location { get; set; } = "";

    [YamlMember(Alias = "hash")]
    public string Hash { get; set; } = "";

    [YamlMember(Alias = "product_code")]
    public string? ProductCode { get; set; }

    [YamlMember(Alias = "upgrade_code")]
    public string? UpgradeCode { get; set; }

    [YamlMember(Alias = "arguments")]
    public List<string>? Arguments { get; set; }
}

/// <summary>
/// Install item for the "installs" array.
/// </summary>
public class InstallItem
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "file";

    [YamlMember(Alias = "path")]
    public string Path { get; set; } = "";

    [YamlMember(Alias = "md5checksum")]
    public string? MD5Checksum { get; set; }

    [YamlMember(Alias = "version")]
    public string? Version { get; set; }
}

/// <summary>
/// Script paths for custom scripts.
/// </summary>
public class ScriptPaths
{
    public string? Preinstall { get; set; }
    public string? Postinstall { get; set; }
    public string? Preuninstall { get; set; }
    public string? Postuninstall { get; set; }
    public string? InstallCheck { get; set; }
    public string? UninstallCheck { get; set; }
}

/// <summary>
/// Extracted metadata from installer.
/// </summary>
public class InstallerMetadata
{
    public string Title { get; set; } = "";
    public string ID { get; set; } = "";
    public string Version { get; set; } = "";
    public string Developer { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public string UpgradeCode { get; set; } = "";
    public string Architecture { get; set; } = "";
    public List<string> SupportedArch { get; set; } = [];
    public string InstallerType { get; set; } = "";
    public List<InstallItem> Installs { get; set; } = [];
    public List<string> Catalogs { get; set; } = [];
    public string RepoPath { get; set; } = "";
    public bool UnattendedInstall { get; set; } = true;
    public bool UnattendedUninstall { get; set; } = true;
    public List<string>? Requires { get; set; }
    public List<string>? UpdateFor { get; set; }
    public List<string>? BlockingApps { get; set; }
}

/// <summary>
/// Cimian import configuration.
/// </summary>
public class ImportConfiguration
{
    public string RepoPath { get; set; } = "";
    public string CloudProvider { get; set; } = "none";
    public string CloudBucket { get; set; } = "";
    public string DefaultCatalog { get; set; } = "Development";
    public string DefaultArch { get; set; } = "x64,arm64";
    public bool OpenImportedYaml { get; set; } = true;
}

/// <summary>
/// YAML wrapper for All.yaml catalog structure.
/// </summary>
public class AllCatalog
{
    [YamlMember(Alias = "items")]
    public List<PkgsInfo> Items { get; set; } = [];
}
