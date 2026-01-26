using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Cimian.CLI.Cimipkg.Models;

/// <summary>
/// Represents the build-info.yaml configuration file for a Cimian package.
/// </summary>
public class BuildInfo
{
    /// <summary>
    /// Product information for the package.
    /// </summary>
    [YamlMember(Alias = "product")]
    public ProductInfo Product { get; set; } = new();

    /// <summary>
    /// Processes dynamic version placeholders in the version field.
    /// Supports ${TIMESTAMP}, ${DATE}, and ${DATETIME} placeholders.
    /// Also supports ${version} in the name field after version is resolved.
    /// </summary>
    public void DoSubstitutions()
    {
        if (Product == null)
            return;

        // Process dynamic version placeholders in the version field first
        // ${TIMESTAMP} -> YYYY.MM.DD.HHMM (e.g., 2025.12.09.1455)
        if (!string.IsNullOrEmpty(Product.Version) && Product.Version.Contains("${TIMESTAMP}"))
        {
            Product.Version = Product.Version.Replace("${TIMESTAMP}", DynamicVersion.Timestamp);
        }

        // ${DATE} -> YYYY.MM.DD (e.g., 2025.12.09)
        if (!string.IsNullOrEmpty(Product.Version) && Product.Version.Contains("${DATE}"))
        {
            Product.Version = Product.Version.Replace("${DATE}", DynamicVersion.Date);
        }

        // ${DATETIME} -> YYYY.MM.DD.HHMMSS (e.g., 2025.12.09.145530)
        if (!string.IsNullOrEmpty(Product.Version) && Product.Version.Contains("${DATETIME}"))
        {
            Product.Version = Product.Version.Replace("${DATETIME}", DynamicVersion.DateTimeStamp);
        }

        // Now process name substitutions - support dynamic placeholders directly in name
        if (!string.IsNullOrEmpty(Product.Name))
        {
            // ${TIMESTAMP} in name
            if (Product.Name.Contains("${TIMESTAMP}"))
            {
                Product.Name = Product.Name.Replace("${TIMESTAMP}", DynamicVersion.Timestamp);
            }

            // ${DATE} in name
            if (Product.Name.Contains("${DATE}"))
            {
                Product.Name = Product.Name.Replace("${DATE}", DynamicVersion.Date);
            }

            // ${DATETIME} in name
            if (Product.Name.Contains("${DATETIME}"))
            {
                Product.Name = Product.Name.Replace("${DATETIME}", DynamicVersion.DateTimeStamp);
            }

            // ${version} in name - uses the already-resolved version value
            if (Product.Name.Contains("${version}") && !string.IsNullOrEmpty(Product.Version))
            {
                Product.Name = Product.Name.Replace("${version}", Product.Version);
            }
        }
    }

    /// <summary>
    /// Installation location for payload files.
    /// Required when payload exists and not an installer package.
    /// </summary>
    [YamlMember(Alias = "install_location")]
    public string? InstallLocation { get; set; }

    /// <summary>
    /// Install arguments for installer packages.
    /// </summary>
    [YamlMember(Alias = "install_arguments")]
    public string? InstallArguments { get; set; }

    /// <summary>
    /// Valid exit codes for installer (comma-separated list like "0,3010").
    /// </summary>
    [YamlMember(Alias = "valid_exit_codes")]
    public string? ValidExitCodes { get; set; }

    /// <summary>
    /// Uninstall arguments for installer packages.
    /// </summary>
    [YamlMember(Alias = "uninstall_arguments")]
    public string? UninstallArguments { get; set; }

    /// <summary>
    /// Software detection by registry uninstall key.
    /// </summary>
    [YamlMember(Alias = "software_detection")]
    public string? SoftwareDetection { get; set; }

    /// <summary>
    /// Action to perform after installation.
    /// Values: none, logout, shutdown, restart
    /// </summary>
    [YamlMember(Alias = "postinstall_action")]
    public string? PostinstallAction { get; set; }

    /// <summary>
    /// Signing certificate subject name (CN=...).
    /// </summary>
    [YamlMember(Alias = "signing_certificate")]
    public string? SigningCertificate { get; set; }

    /// <summary>
    /// Signing certificate thumbprint (alternative to subject name).
    /// </summary>
    [YamlMember(Alias = "signing_thumbprint")]
    public string? SigningThumbprint { get; set; }

    /// <summary>
    /// Package signature metadata (populated during build).
    /// </summary>
    [YamlMember(Alias = "signature")]
    public PackageSignature? Signature { get; set; }

    /// <summary>
    /// Description of the package.
    /// </summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>
    /// Minimum OS version requirement.
    /// </summary>
    [YamlMember(Alias = "minimum_os_version")]
    public string? MinimumOsVersion { get; set; }

    /// <summary>
    /// Category for organizing packages.
    /// </summary>
    [YamlMember(Alias = "category")]
    public string? Category { get; set; }

    /// <summary>
    /// Icon URL for the package.
    /// </summary>
    [YamlMember(Alias = "icon")]
    public string? Icon { get; set; }

    /// <summary>
    /// Blocking applications list.
    /// </summary>
    [YamlMember(Alias = "blocking_applications")]
    public List<string>? BlockingApplications { get; set; }

    /// <summary>
    /// Override uninstall script (for nupkg format).
    /// </summary>
    [YamlMember(Alias = "override_uninstall_script")]
    public bool OverrideUninstallScript { get; set; }

    /// <summary>
    /// Determines if this is an installer package based on installer_type.
    /// </summary>
    public bool IsInstallerPackage => !string.IsNullOrEmpty(Product?.InstallerType);
}

/// <summary>
/// Product information section of build-info.yaml.
/// </summary>
public class ProductInfo
{
    /// <summary>
    /// Product name (used in package filename).
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Product version (YYYY.MM.DD or semantic version).
    /// </summary>
    [YamlMember(Alias = "version")]
    public string Version { get; set; } = "1.0.0";

    /// <summary>
    /// Developer/publisher name.
    /// </summary>
    [YamlMember(Alias = "developer")]
    public string? Developer { get; set; }

    /// <summary>
    /// Unique package identifier (e.g., com.company.productname).
    /// </summary>
    [YamlMember(Alias = "identifier")]
    public string Identifier { get; set; } = string.Empty;

    /// <summary>
    /// Installer type (msi, exe, etc.) - indicates this is an installer package.
    /// </summary>
    [YamlMember(Alias = "installer_type")]
    public string? InstallerType { get; set; }

    /// <summary>
    /// URL to the product or download page.
    /// </summary>
    [YamlMember(Alias = "url")]
    public string? Url { get; set; }

    /// <summary>
    /// Product copyright.
    /// </summary>
    [YamlMember(Alias = "copyright")]
    public string? Copyright { get; set; }

    /// <summary>
    /// Product license URL.
    /// </summary>
    [YamlMember(Alias = "license")]
    public string? License { get; set; }

    /// <summary>
    /// Product tags for categorization.
    /// </summary>
    [YamlMember(Alias = "tags")]
    public List<string>? Tags { get; set; }
}

/// <summary>
/// Package signature metadata embedded in build-info.yaml.
/// </summary>
public class PackageSignature
{
    /// <summary>
    /// Signing algorithm used (e.g., SHA256).
    /// </summary>
    [YamlMember(Alias = "algorithm")]
    public string Algorithm { get; set; } = "SHA256";

    /// <summary>
    /// Certificate information.
    /// </summary>
    [YamlMember(Alias = "certificate")]
    public CertificateInfo Certificate { get; set; } = new();

    /// <summary>
    /// Hash of the entire package.
    /// </summary>
    [YamlMember(Alias = "package_hash")]
    public string PackageHash { get; set; } = string.Empty;

    /// <summary>
    /// Hash of package contents (excluding build-info.yaml).
    /// </summary>
    [YamlMember(Alias = "content_hash")]
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Signed hash (hash + thumbprint).
    /// </summary>
    [YamlMember(Alias = "signed_hash")]
    public string SignedHash { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when signature was created.
    /// </summary>
    [YamlMember(Alias = "timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    /// <summary>
    /// Signature format version.
    /// </summary>
    [YamlMember(Alias = "version")]
    public string Version { get; set; } = "1.0";
}

/// <summary>
/// Certificate information for package signing.
/// </summary>
public class CertificateInfo
{
    /// <summary>
    /// Certificate subject (CN=...).
    /// </summary>
    [YamlMember(Alias = "subject")]
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Certificate issuer.
    /// </summary>
    [YamlMember(Alias = "issuer")]
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// Certificate thumbprint (SHA1).
    /// </summary>
    [YamlMember(Alias = "thumbprint")]
    public string Thumbprint { get; set; } = string.Empty;

    /// <summary>
    /// Certificate serial number.
    /// </summary>
    [YamlMember(Alias = "serial_number")]
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>
    /// Certificate validity start date.
    /// </summary>
    [YamlMember(Alias = "not_before")]
    public string NotBefore { get; set; } = string.Empty;

    /// <summary>
    /// Certificate validity end date.
    /// </summary>
    [YamlMember(Alias = "not_after")]
    public string NotAfter { get; set; } = string.Empty;
}

/// <summary>
/// Generates dynamic date/time based version strings.
/// </summary>
public static class DynamicVersion
{
    /// <summary>
    /// Current date/time formatted as YYYY.MM.DD.HHMM (e.g., 2025.12.09.1455).
    /// </summary>
    public static string Timestamp => System.DateTime.Now.ToString("yyyy.MM.dd.HHmm");

    /// <summary>
    /// Current date formatted as YYYY.MM.DD (e.g., 2025.12.09).
    /// </summary>
    public static string Date => System.DateTime.Now.ToString("yyyy.MM.dd");

    /// <summary>
    /// Current date/time formatted as YYYY.MM.DD.HHMMSS (e.g., 2025.12.09.145530).
    /// </summary>
    public static string DateTimeStamp => System.DateTime.Now.ToString("yyyy.MM.dd.HHmmss");
}
