using System.Runtime.InteropServices;

namespace Cimian.Core.Models;

/// <summary>
/// Represents system facts used for conditional evaluation
/// Migrated from Go pkg/predicates/predicates.go FactsCollector
/// </summary>
public class SystemFacts
{
    /// <summary>
    /// Computer hostname
    /// </summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    /// System architecture (x64, x86, arm64)
    /// Maps to both 'arch' and 'architecture' fact keys for compatibility
    /// </summary>
    public string Architecture { get; set; } = string.Empty;

    /// <summary>
    /// Operating system name
    /// </summary>
    public string OperatingSystem { get; set; } = string.Empty;

    /// <summary>
    /// Operating system version string (e.g., "10.0.22621")
    /// </summary>
    public string OperatingSystemVersion { get; set; } = string.Empty;

    /// <summary>
    /// Operating system build number
    /// </summary>
    public string OperatingSystemBuild { get; set; } = string.Empty;

    /// <summary>
    /// Major OS version number (e.g., 10 for Windows 10, 11 for Windows 11)
    /// Maps to 'os_vers_major' fact key
    /// </summary>
    public int OSVersMajor { get; set; }

    /// <summary>
    /// Minor OS version number
    /// Maps to 'os_vers_minor' fact key
    /// </summary>
    public int OSVersMinor { get; set; }

    /// <summary>
    /// OS build version number
    /// Maps to 'os_build_number' fact key
    /// </summary>
    public int OSBuildNumber { get; set; }

    /// <summary>
    /// Active Directory domain name (if domain-joined)
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    /// Whether the system is domain-joined
    /// </summary>
    public bool IsDomainJoined { get; set; }

    /// <summary>
    /// Whether the system is enrolled in MDM (Intune)
    /// </summary>
    public bool IsEnrolled { get; set; }

    /// <summary>
    /// Current logged-in username
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Machine type: laptop, desktop, virtual, server
    /// Maps to 'machine_type' fact key
    /// </summary>
    public string MachineType { get; set; } = string.Empty;

    /// <summary>
    /// Machine model (e.g., "Dell OptiPlex 7090", "ThinkPad X1 Carbon")
    /// Maps to 'machine_model' fact key
    /// </summary>
    public string MachineModel { get; set; } = string.Empty;

    /// <summary>
    /// Domain join type: workgroup, domain, entra, hybrid
    /// Maps to 'joined_type' fact key
    /// </summary>
    public string JoinedType { get; set; } = string.Empty;

    /// <summary>
    /// Battery state: connected, disconnected, unknown
    /// Maps to 'battery_state' fact key
    /// </summary>
    public string BatteryState { get; set; } = string.Empty;

    /// <summary>
    /// Current date in YYYY-MM-DD format
    /// Maps to 'date' fact key
    /// </summary>
    public string Date { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");

    /// <summary>
    /// List of catalog names this machine is assigned to
    /// Maps to 'catalogs' fact key (used with ANY operator)
    /// </summary>
    public List<string> Catalogs { get; set; } = new();

    /// <summary>
    /// List of installed software (name-version pairs)
    /// </summary>
    public Dictionary<string, string> InstalledSoftware { get; set; } = new();

    /// <summary>
    /// Environment variables accessible for evaluation
    /// </summary>
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();

    /// <summary>
    /// Registry values accessible for evaluation
    /// </summary>
    public Dictionary<string, object> RegistryValues { get; set; } = new();

    /// <summary>
    /// Network interfaces and their properties
    /// </summary>
    public List<NetworkInterface> NetworkInterfaces { get; set; } = new();

    /// <summary>
    /// System uptime in seconds
    /// </summary>
    public long UptimeSeconds { get; set; }

    /// <summary>
    /// Total system memory in bytes
    /// </summary>
    public long TotalMemoryBytes { get; set; }

    /// <summary>
    /// Available system memory in bytes
    /// </summary>
    public long AvailableMemoryBytes { get; set; }

    /// <summary>
    /// Processor information
    /// </summary>
    public ProcessorInfo? Processor { get; set; }

    /// <summary>
    /// Last boot time
    /// </summary>
    public DateTime LastBootTime { get; set; }

    /// <summary>
    /// Time when facts were collected
    /// </summary>
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Additional custom facts that can be added by plugins or extensions
    /// </summary>
    public Dictionary<string, object> CustomFacts { get; set; } = new();

    /// <summary>
    /// Gets a fact value by name with case-insensitive lookup
    /// Matches Go implementation's GetAllFacts() mapping
    /// </summary>
    public object? GetFactValue(string factName)
    {
        return factName.ToLowerInvariant() switch
        {
            // Core facts matching Go implementation
            "hostname" => Hostname,
            "arch" => Architecture,
            "architecture" => Architecture,
            "os_version" => OperatingSystemVersion,
            "os_vers_major" => OSVersMajor,
            "os_vers_minor" => OSVersMinor,
            "os_build_number" => OSBuildNumber,
            "domain" => Domain,
            "username" => Username,
            "machine_type" => MachineType,
            "machine_model" => MachineModel,
            "joined_type" => JoinedType,
            "battery_state" => BatteryState,
            "date" => Date,
            "catalogs" => Catalogs,
            
            // Legacy mappings
            "operatingsystem" => OperatingSystem,
            "operatingsystemversion" => OperatingSystemVersion,
            "operatingsystembuild" => OperatingSystemBuild,
            "isdomainjoined" => IsDomainJoined,
            "isenrolled" => IsEnrolled,
            "uptimeseconds" => UptimeSeconds,
            "totalmemorybytes" => TotalMemoryBytes,
            "availablememorybytes" => AvailableMemoryBytes,
            
            _ => CustomFacts.GetValueOrDefault(factName) ?? 
                 EnvironmentVariables.GetValueOrDefault(factName) ?? 
                 RegistryValues.GetValueOrDefault(factName)
        };
    }

    /// <summary>
    /// Checks if a software package is installed
    /// </summary>
    public bool IsSoftwareInstalled(string packageName)
    {
        return InstalledSoftware.ContainsKey(packageName);
    }

    /// <summary>
    /// Gets the version of an installed software package
    /// </summary>
    public string? GetSoftwareVersion(string packageName)
    {
        return InstalledSoftware.GetValueOrDefault(packageName);
    }
}

/// <summary>
/// Represents a network interface
/// </summary>
public class NetworkInterface
{
    /// <summary>
    /// Interface name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Interface type (Ethernet, Wireless, etc.)
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// MAC address
    /// </summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>
    /// IP addresses assigned to this interface
    /// </summary>
    public List<string> IPAddresses { get; set; } = new();

    /// <summary>
    /// Whether the interface is currently active
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Interface speed in Mbps
    /// </summary>
    public long? SpeedMbps { get; set; }
}

/// <summary>
/// Represents processor information
/// </summary>
public class ProcessorInfo
{
    /// <summary>
    /// Processor name/model
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Number of physical cores
    /// </summary>
    public int PhysicalCores { get; set; }

    /// <summary>
    /// Number of logical cores (including hyperthreading)
    /// </summary>
    public int LogicalCores { get; set; }

    /// <summary>
    /// Base clock speed in MHz
    /// </summary>
    public int ClockSpeedMHz { get; set; }

    /// <summary>
    /// Processor architecture
    /// </summary>
    public string Architecture { get; set; } = string.Empty;
}
