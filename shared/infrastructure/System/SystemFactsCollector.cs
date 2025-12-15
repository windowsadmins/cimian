using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Cimian.Core.Models;

namespace Cimian.Infrastructure.System;

/// <summary>
/// Interface for system facts collection
/// </summary>
public interface ISystemFactsCollector
{
    /// <summary>
    /// Collects all system facts
    /// </summary>
    Task<SystemFacts> CollectAsync();
    
    /// <summary>
    /// Adds a custom fact
    /// </summary>
    void AddCustomFact(string key, object value);
    
    /// <summary>
    /// Sets the catalogs this machine is assigned to
    /// </summary>
    void SetCatalogs(IEnumerable<string> catalogs);
}

/// <summary>
/// Collects system facts using WMI and other Windows APIs
/// Migrated from Go pkg/predicates/predicates.go FactsCollector
/// </summary>
public class SystemFactsCollector : ISystemFactsCollector
{
    private readonly ILogger<SystemFactsCollector> _logger;
    private readonly Dictionary<string, object> _customFacts = new();
    private List<string> _catalogs = new();

    public SystemFactsCollector(ILogger<SystemFactsCollector> logger)
    {
        _logger = logger;
    }

    public void AddCustomFact(string key, object value)
    {
        _customFacts[key] = value;
    }

    public void SetCatalogs(IEnumerable<string> catalogs)
    {
        _catalogs = catalogs.ToList();
    }

    public async Task<SystemFacts> CollectAsync()
    {
        _logger.LogDebug("Collecting system facts...");
        
        var facts = new SystemFacts
        {
            CollectedAt = DateTime.UtcNow,
            Date = DateTime.Now.ToString("yyyy-MM-dd"),
            Catalogs = _catalogs
        };

        try
        {
            // Collect facts - these can be done in parallel
            var tasks = new List<Task>
            {
                Task.Run(() => CollectBasicInfo(facts)),
                Task.Run(() => CollectOSInfo(facts)),
                Task.Run(() => CollectMachineInfo(facts)),
                Task.Run(() => CollectDomainInfo(facts)),
                Task.Run(() => CollectMemoryInfo(facts)),
                Task.Run(() => CollectProcessorInfo(facts))
            };

            await Task.WhenAll(tasks);

            // Add custom facts
            foreach (var (key, value) in _customFacts)
            {
                facts.CustomFacts[key] = value;
            }

            _logger.LogInformation("System facts collected: Hostname={Hostname}, Arch={Arch}, OS={OS}, JoinedType={JoinedType}",
                facts.Hostname, facts.Architecture, facts.OperatingSystem, facts.JoinedType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error collecting system facts");
        }

        return facts;
    }

    private void CollectBasicInfo(SystemFacts facts)
    {
        try
        {
            facts.Hostname = Environment.MachineName;
            facts.Username = Environment.UserName;
            facts.Architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
            
            // Normalize architecture names to match Go implementation
            facts.Architecture = facts.Architecture switch
            {
                "x64" => "x64",
                "x86" => "x86",
                "arm64" => "ARM64",
                "arm" => "ARM",
                _ => facts.Architecture
            };

            facts.OperatingSystem = RuntimeInformation.OSDescription;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect basic system info");
        }
    }

    private void CollectOSInfo(SystemFacts facts)
    {
        try
        {
            var os = Environment.OSVersion;
            facts.OSVersMajor = os.Version.Major;
            facts.OSVersMinor = os.Version.Minor;
            facts.OSBuildNumber = os.Version.Build;
            facts.OperatingSystemVersion = $"{os.Version.Major}.{os.Version.Minor}.{os.Version.Build}";
            facts.OperatingSystemBuild = os.Version.Build.ToString();

            // Get detailed OS info from WMI
            using var searcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in searcher.Get())
            {
                facts.OperatingSystem = mo["Caption"]?.ToString() ?? facts.OperatingSystem;
                facts.OperatingSystemVersion = mo["Version"]?.ToString() ?? facts.OperatingSystemVersion;
                facts.OperatingSystemBuild = mo["BuildNumber"]?.ToString() ?? facts.OperatingSystemBuild;
            }

            // Calculate last boot time
            using var bootSearcher = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in bootSearcher.Get())
            {
                var lastBootStr = mo["LastBootUpTime"]?.ToString();
                if (!string.IsNullOrEmpty(lastBootStr))
                {
                    facts.LastBootTime = ManagementDateTimeConverter.ToDateTime(lastBootStr);
                    facts.UptimeSeconds = (long)(DateTime.Now - facts.LastBootTime).TotalSeconds;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect OS info");
        }
    }

    private void CollectMachineInfo(SystemFacts facts)
    {
        try
        {
            // Get machine model from WMI
            using var modelSearcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in modelSearcher.Get())
            {
                var manufacturer = mo["Manufacturer"]?.ToString() ?? "";
                var model = mo["Model"]?.ToString() ?? "";
                
                if (!string.IsNullOrEmpty(manufacturer) && !string.IsNullOrEmpty(model))
                {
                    facts.MachineModel = $"{manufacturer} {model}";
                }
                else if (!string.IsNullOrEmpty(model))
                {
                    facts.MachineModel = model;
                }
                else if (!string.IsNullOrEmpty(manufacturer))
                {
                    facts.MachineModel = manufacturer;
                }
            }

            // Determine machine type (laptop, desktop, virtual, server)
            facts.MachineType = DetermineMachineType();
            facts.BatteryState = GetBatteryState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect machine info");
        }
    }

    private string DetermineMachineType()
    {
        try
        {
            // Check if running in a virtual machine
            using var vmSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in vmSearcher.Get())
            {
                var model = mo["Model"]?.ToString()?.ToLowerInvariant() ?? "";
                var manufacturer = mo["Manufacturer"]?.ToString()?.ToLowerInvariant() ?? "";

                // Check for common VM indicators
                if (model.Contains("virtual") || manufacturer.Contains("vmware") || 
                    manufacturer.Contains("microsoft corporation") && model.Contains("virtual") ||
                    manufacturer.Contains("xen") || model.Contains("kvm") ||
                    manufacturer.Contains("qemu"))
                {
                    return "virtual";
                }
            }

            // Check chassis type for laptop vs desktop
            using var chassisSearcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (ManagementObject mo in chassisSearcher.Get())
            {
                var chassisTypes = mo["ChassisTypes"] as ushort[];
                if (chassisTypes != null && chassisTypes.Length > 0)
                {
                    // Laptop chassis types: 8=Portable, 9=Laptop, 10=Notebook, 14=Sub Notebook, 31=Convertible, 32=Detachable
                    // Desktop chassis types: 3=Desktop, 4=Low Profile Desktop, 5=Pizza Box, 6=Mini Tower, 7=Tower
                    // Server chassis types: 23=Rack Mount Chassis, 25=Multi-system Chassis
                    var laptopTypes = new ushort[] { 8, 9, 10, 14, 31, 32 };
                    var serverTypes = new ushort[] { 23, 25 };

                    foreach (var chassisType in chassisTypes)
                    {
                        if (laptopTypes.Contains(chassisType))
                        {
                            return "laptop";
                        }
                        if (serverTypes.Contains(chassisType))
                        {
                            return "server";
                        }
                    }
                }
            }

            // Check for battery as additional laptop indicator
            using var batterySearcher = new ManagementObjectSearcher("SELECT * FROM Win32_Battery");
            if (batterySearcher.Get().Count > 0)
            {
                return "laptop";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to determine machine type");
        }

        return "desktop";
    }

    private string GetBatteryState()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT BatteryStatus FROM Win32_Battery");
            foreach (ManagementObject mo in searcher.Get())
            {
                var status = Convert.ToInt32(mo["BatteryStatus"]);
                // 1=Discharging, 2=AC Power, 3-10=Various charging states
                return status == 1 ? "disconnected" : "connected";
            }
        }
        catch
        {
            // No battery or error - ignore
        }

        return "unknown";
    }

    private void CollectDomainInfo(SystemFacts facts)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Domain, PartOfDomain, Workgroup, DomainRole FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in searcher.Get())
            {
                facts.Domain = mo["Domain"]?.ToString();
                facts.IsDomainJoined = Convert.ToBoolean(mo["PartOfDomain"]);

                if (facts.IsDomainJoined)
                {
                    // Check if it's Entra joined
                    if (IsEntraJoined())
                    {
                        // Check for hybrid join
                        if (!string.IsNullOrEmpty(facts.Domain) && !facts.Domain.Equals("WORKGROUP", StringComparison.OrdinalIgnoreCase))
                        {
                            facts.JoinedType = "hybrid";
                        }
                        else
                        {
                            facts.JoinedType = "entra";
                        }
                    }
                    else
                    {
                        facts.JoinedType = "domain";
                    }
                }
                else
                {
                    facts.JoinedType = "workgroup";
                }
            }

            // Check for MDM enrollment
            facts.IsEnrolled = CheckMdmEnrollment();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect domain info");
            facts.JoinedType = "unknown";
        }
    }

    private bool IsEntraJoined()
    {
        try
        {
            // Check for Entra join via registry
            // HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo");
            if (key != null)
            {
                var subKeys = key.GetSubKeyNames();
                if (subKeys.Length > 0)
                {
                    return true;
                }
            }

            // Alternative: Check domain name patterns
            using var searcher = new ManagementObjectSearcher("SELECT Domain FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in searcher.Get())
            {
                var domain = mo["Domain"]?.ToString()?.ToLowerInvariant() ?? "";
                if (domain.Contains(".onmicrosoft.com") || domain.Contains("azuread"))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Registry access failed - continue
        }

        return false;
    }

    private bool CheckMdmEnrollment()
    {
        try
        {
            // Check for Intune enrollment via registry
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments");
            if (key != null)
            {
                var subKeys = key.GetSubKeyNames();
                foreach (var subKey in subKeys)
                {
                    using var enrollmentKey = key.OpenSubKey(subKey);
                    var enrollmentType = enrollmentKey?.GetValue("EnrollmentType");
                    if (enrollmentType != null && Convert.ToInt32(enrollmentType) > 0)
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Registry access failed - continue
        }

        return false;
    }

    private void CollectMemoryInfo(SystemFacts facts)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in searcher.Get())
            {
                facts.TotalMemoryBytes = Convert.ToInt64(mo["TotalPhysicalMemory"]);
            }

            using var memSearcher = new ManagementObjectSearcher("SELECT FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in memSearcher.Get())
            {
                // FreePhysicalMemory is in KB
                facts.AvailableMemoryBytes = Convert.ToInt64(mo["FreePhysicalMemory"]) * 1024;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect memory info");
        }
    }

    private void CollectProcessorInfo(SystemFacts facts)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject mo in searcher.Get())
            {
                facts.Processor = new ProcessorInfo
                {
                    Name = mo["Name"]?.ToString() ?? "Unknown",
                    PhysicalCores = Convert.ToInt32(mo["NumberOfCores"]),
                    LogicalCores = Convert.ToInt32(mo["NumberOfLogicalProcessors"]),
                    ClockSpeedMHz = Convert.ToInt32(mo["MaxClockSpeed"]),
                    Architecture = facts.Architecture
                };
                break; // Only get first processor
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect processor info");
        }
    }
}
