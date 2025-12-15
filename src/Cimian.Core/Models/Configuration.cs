using YamlDotNet.Serialization;

namespace Cimian.Core.Models;

/// <summary>
/// Represents Cimian configuration settings
/// Migrated from Go struct: config.Configuration
/// </summary>
public class Configuration
{
    /// <summary>
    /// Base URL for the software catalog
    /// </summary>
    [YamlMember(Alias = "catalog_url")]
    public string? CatalogUrl { get; set; }

    /// <summary>
    /// Local path to catalog files
    /// </summary>
    [YamlMember(Alias = "catalog_path")]
    public string? CatalogPath { get; set; }

    /// <summary>
    /// Base URL for package downloads
    /// </summary>
    [YamlMember(Alias = "package_url")]
    public string? PackageUrl { get; set; }

    /// <summary>
    /// Local path for package storage
    /// </summary>
    [YamlMember(Alias = "package_path")]
    public string? PackagePath { get; set; }

    /// <summary>
    /// Maximum concurrent downloads
    /// </summary>
    [YamlMember(Alias = "max_concurrent_downloads")]
    public int MaxConcurrentDownloads { get; set; } = 4;

    /// <summary>
    /// Download timeout in seconds
    /// </summary>
    [YamlMember(Alias = "download_timeout")]
    public int DownloadTimeout { get; set; } = 300;

    /// <summary>
    /// Installation timeout in seconds
    /// </summary>
    [YamlMember(Alias = "installation_timeout")]
    public int InstallationTimeout { get; set; } = 1800;

    /// <summary>
    /// Whether to verify package hashes
    /// </summary>
    [YamlMember(Alias = "verify_hashes")]
    public bool VerifyHashes { get; set; } = true;

    /// <summary>
    /// Whether to use cached packages
    /// </summary>
    [YamlMember(Alias = "use_cache")]
    public bool UseCache { get; set; } = true;

    /// <summary>
    /// Cache retention period in days
    /// </summary>
    [YamlMember(Alias = "cache_retention_days")]
    public int CacheRetentionDays { get; set; } = 30;

    /// <summary>
    /// Log level (Debug, Info, Warning, Error)
    /// </summary>
    [YamlMember(Alias = "log_level")]
    public string LogLevel { get; set; } = "Info";

    /// <summary>
    /// Log file path
    /// </summary>
    [YamlMember(Alias = "log_path")]
    public string? LogPath { get; set; }

    /// <summary>
    /// Whether to enable verbose logging
    /// </summary>
    [YamlMember(Alias = "verbose")]
    public bool Verbose { get; set; }

    /// <summary>
    /// Proxy configuration
    /// </summary>
    [YamlMember(Alias = "proxy")]
    public ProxyConfiguration? Proxy { get; set; }

    /// <summary>
    /// Enterprise/cloud configuration
    /// </summary>
    [YamlMember(Alias = "cloud")]
    public CloudConfiguration? Cloud { get; set; }

    /// <summary>
    /// Additional custom settings
    /// </summary>
    [YamlMember(Alias = "custom")]
    public Dictionary<string, object>? Custom { get; set; }
}

/// <summary>
/// Proxy configuration settings
/// </summary>
public class ProxyConfiguration
{
    /// <summary>
    /// Proxy server URL
    /// </summary>
    [YamlMember(Alias = "url")]
    public string? Url { get; set; }

    /// <summary>
    /// Proxy username
    /// </summary>
    [YamlMember(Alias = "username")]
    public string? Username { get; set; }

    /// <summary>
    /// Proxy password
    /// </summary>
    [YamlMember(Alias = "password")]
    public string? Password { get; set; }

    /// <summary>
    /// Domains to bypass proxy for
    /// </summary>
    [YamlMember(Alias = "bypass")]
    public List<string>? Bypass { get; set; }
}

/// <summary>
/// Cloud/enterprise configuration settings
/// </summary>
public class CloudConfiguration
{
    /// <summary>
    /// AWS S3 configuration
    /// </summary>
    [YamlMember(Alias = "aws")]
    public AwsConfiguration? Aws { get; set; }

    /// <summary>
    /// Azure Blob configuration
    /// </summary>
    [YamlMember(Alias = "azure")]
    public AzureConfiguration? Azure { get; set; }

    /// <summary>
    /// Intune/MDM integration settings
    /// </summary>
    [YamlMember(Alias = "intune")]
    public IntuneConfiguration? Intune { get; set; }
}

/// <summary>
/// AWS S3 configuration
/// </summary>
public class AwsConfiguration
{
    /// <summary>
    /// AWS region
    /// </summary>
    [YamlMember(Alias = "region")]
    public string? Region { get; set; }

    /// <summary>
    /// S3 bucket name
    /// </summary>
    [YamlMember(Alias = "bucket")]
    public string? Bucket { get; set; }

    /// <summary>
    /// Access key ID
    /// </summary>
    [YamlMember(Alias = "access_key_id")]
    public string? AccessKeyId { get; set; }

    /// <summary>
    /// Secret access key
    /// </summary>
    [YamlMember(Alias = "secret_access_key")]
    public string? SecretAccessKey { get; set; }
}

/// <summary>
/// Azure Blob configuration
/// </summary>
public class AzureConfiguration
{
    /// <summary>
    /// Storage account name
    /// </summary>
    [YamlMember(Alias = "account_name")]
    public string? AccountName { get; set; }

    /// <summary>
    /// Storage account key
    /// </summary>
    [YamlMember(Alias = "account_key")]
    public string? AccountKey { get; set; }

    /// <summary>
    /// Container name
    /// </summary>
    [YamlMember(Alias = "container")]
    public string? Container { get; set; }
}

/// <summary>
/// Intune/MDM configuration
/// </summary>
public class IntuneConfiguration
{
    /// <summary>
    /// Tenant ID
    /// </summary>
    [YamlMember(Alias = "tenant_id")]
    public string? TenantId { get; set; }

    /// <summary>
    /// Application ID
    /// </summary>
    [YamlMember(Alias = "app_id")]
    public string? AppId { get; set; }

    /// <summary>
    /// Application secret
    /// </summary>
    [YamlMember(Alias = "app_secret")]
    public string? AppSecret { get; set; }
}
