using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Cimian.Core;
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
                Task.Run(() => CollectProcessorInfo(facts)),
                Task.Run(() => CollectGpuInfo(facts)),
                Task.Run(() => CollectNpuInfo(facts)),
                Task.Run(() => CollectRamInfo(facts)),
                Task.Run(() => CollectStorageInfo(facts))
            };

            await Task.WhenAll(tasks);

            // Add custom facts
            foreach (var (key, value) in _customFacts)
            {
                facts.CustomFacts[key] = value;
            }

            _logger.LogInformation("System facts collected: Hostname={Hostname}, Arch={Arch}, OS={OS}, JoinedType={JoinedType}, GPU={Gpu}, CPU={Cpu}, NPU={Npu}, RAM={Ram}GB {RamType}, Storage={Storage}",
                facts.Hostname, facts.Architecture, facts.OperatingSystem, facts.JoinedType,
                facts.GpuNames.Count > 0 ? string.Join(", ", facts.GpuNames) : "none",
                facts.CpuName, facts.NpuAvailable ? facts.NpuName : "none",
                facts.RamTotalGb, facts.RamType, facts.StorageType);
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

            // Friendly model name lives on Win32_ComputerSystemProduct.Version on Lenovo
            // (Win32_ComputerSystem.Model returns the MTM code there).
            try
            {
                using var productSearcher = new ManagementObjectSearcher("SELECT Version FROM Win32_ComputerSystemProduct");
                foreach (ManagementObject mo in productSearcher.Get())
                {
                    var version = mo["Version"]?.ToString()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(version))
                    {
                        facts.ModelVersion = version;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read Win32_ComputerSystemProduct.Version");
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
            using var searcher = new ManagementObjectSearcher("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject mo in searcher.Get())
            {
                var rawName = mo["Name"]?.ToString() ?? "Unknown";
                var manufacturer = mo["Manufacturer"]?.ToString() ?? "";
                var cores = Convert.ToInt32(mo["NumberOfCores"]);
                var logicalCores = Convert.ToInt32(mo["NumberOfLogicalProcessors"]);
                var clockSpeed = Convert.ToInt32(mo["MaxClockSpeed"]);

                facts.Processor = new ProcessorInfo
                {
                    Name = rawName,
                    PhysicalCores = cores,
                    LogicalCores = logicalCores,
                    ClockSpeedMHz = clockSpeed,
                    Architecture = facts.Architecture
                };

                // Populate predicate-friendly CPU facts
                facts.CpuName = CleanProcessorName(rawName);
                facts.CpuManufacturer = MapCpuManufacturer(manufacturer, rawName);
                facts.CpuCores = cores;
                facts.CpuLogicalCores = logicalCores;
                break; // Only get first processor
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect processor info");
        }
    }

    // ==========================================================================
    // GPU Collection
    // ==========================================================================

    /// <summary>
    /// Collects display adapter facts.
    ///
    /// Win32_VideoController only reports a GPU's model name while its vendor driver is
    /// bound. Once that driver is missing the card either falls back to a generic adapter
    /// name or drops out of the Display class entirely, which used to make driver
    /// predicates like 'gpu_names CONTAINS "Quadro"' stop matching on exactly the machines
    /// that need the driver reinstalled. So identity is also read from the PCI enumerator,
    /// and model names seen in earlier runs are remembered per hardware ID.
    /// </summary>
    private void CollectGpuInfo(SystemFacts facts)
    {
        var adapters = new List<GpuAdapter>();

        try
        {
            CollectVideoControllers(adapters);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query Win32_VideoController for GPU info");
        }

        try
        {
            MergePnpGpuDevices(adapters);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query Win32_PnPEntity for GPU hardware IDs");
        }

        RestoreLastKnownGpuNames(adapters);

        string? discreteGpuName = null;
        string? discreteDriverVersion = null;
        long discreteVram = 0;

        foreach (var adapter in adapters)
        {
            if (!string.IsNullOrWhiteSpace(adapter.Name))
                facts.GpuNames.Add(adapter.Name);

            if (adapter.PciId.Length > 0 && !facts.GpuPciIds.Contains(adapter.PciId))
                facts.GpuPciIds.Add(adapter.PciId);

            var vendor = GpuIdentity.VendorFromPciId(adapter.PciId);
            if (vendor.Length > 0 && !facts.GpuVendors.Contains(vendor))
                facts.GpuVendors.Add(vendor);

            // Prioritize discrete GPUs for driver version and VRAM
            if (IsDiscreteGpu(adapter.Name))
            {
                discreteGpuName = adapter.Name;
                discreteDriverVersion = adapter.DriverVersion;
                discreteVram = adapter.VramBytes;
            }
            else if (discreteGpuName == null)
            {
                // Use first GPU as fallback if no discrete found yet
                discreteDriverVersion = adapter.DriverVersion;
                discreteVram = adapter.VramBytes;
            }
        }

        facts.GpuDriverVersion = discreteDriverVersion ?? "";
        facts.GpuVramGb = RoundToCommonVramSize(discreteVram);
        facts.GpuDriverMissing = adapters.Any(a => a.DriverMissing);

        _logger.LogDebug("GPU info collected: {Count} GPUs, primary={Primary}, driver={Driver}, VRAM={Vram}GB, pci=[{Pci}], driverMissing={DriverMissing}, namesRestoredFromCache={Restored}",
            facts.GpuNames.Count,
            discreteGpuName ?? (facts.GpuNames.Count > 0 ? facts.GpuNames[0] : "none"),
            facts.GpuDriverVersion, facts.GpuVramGb,
            string.Join(", ", facts.GpuPciIds), facts.GpuDriverMissing,
            adapters.Count(a => a.RestoredFromCache));
    }

    /// <summary>
    /// Reads adapters Windows has bound a display driver to.
    /// </summary>
    private void CollectVideoControllers(List<GpuAdapter> adapters)
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DriverVersion, AdapterRAM, PNPDeviceID FROM Win32_VideoController");

        foreach (ManagementObject mo in searcher.Get())
        {
            var name = mo["Name"]?.ToString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var instanceId = mo["PNPDeviceID"]?.ToString() ?? "";

            adapters.Add(new GpuAdapter
            {
                InstanceId = instanceId,
                PciId = GpuIdentity.NormalizePciId(instanceId),
                Name = name,
                NameIsGeneric = GpuIdentity.IsGenericAdapterName(name),
                DriverVersion = mo["DriverVersion"]?.ToString() ?? "",
                VramBytes = ToInt64(mo["AdapterRAM"])
            });
        }
    }

    /// <summary>
    /// Adds GPUs the PCI enumerator can see but Win32_VideoController cannot. A card with
    /// no driver installed lands under "Other devices" with no PNPClass and a placeholder
    /// name such as "3D Video Controller", so it never appears as a display adapter.
    /// </summary>
    private void MergePnpGpuDevices(List<GpuAdapter> adapters)
    {
        // Doubled backslashes are WQL escaping: this matches PNP device IDs starting "PCI\VEN".
        // The trailing underscore is left off the pattern because '_' is a WQL wildcard.
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Name, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE DeviceID LIKE 'PCI\\\\VEN%'");

        foreach (ManagementObject mo in searcher.Get())
        {
            var deviceId = mo["DeviceID"]?.ToString() ?? "";
            var pciId = GpuIdentity.NormalizePciId(deviceId);
            if (pciId.Length == 0)
                continue;

            var pnpClass = mo["PNPClass"]?.ToString() ?? "";
            var name = mo["Name"]?.ToString()?.Trim() ?? "";
            var errorCode = (int)ToInt64(mo["ConfigManagerErrorCode"]);

            var isDisplayClass = string.Equals(pnpClass, "Display", StringComparison.OrdinalIgnoreCase);
            var isUnclaimed = pnpClass.Length == 0
                || string.Equals(pnpClass, "Unknown", StringComparison.OrdinalIgnoreCase);
            if (!isDisplayClass && !(isUnclaimed && GpuIdentity.IsGenericAdapterName(name)))
                continue;

            // Match on instance ID, falling back to name for the rare adapter that reports
            // no PNPDeviceID - otherwise it would be counted twice.
            var existing = adapters.FirstOrDefault(a =>
                    string.Equals(a.InstanceId, deviceId, StringComparison.OrdinalIgnoreCase))
                ?? adapters.FirstOrDefault(a =>
                    a.PciId.Length == 0 && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.InstanceId = deviceId;
                existing.PciId = pciId;
                existing.ConfigManagerErrorCode = errorCode;
                continue;
            }

            adapters.Add(new GpuAdapter
            {
                InstanceId = deviceId,
                PciId = pciId,
                Name = name,
                NameIsGeneric = GpuIdentity.IsGenericAdapterName(name),
                ConfigManagerErrorCode = errorCode
            });
        }
    }

    /// <summary>
    /// Remembers every model name a vendor driver currently reports, and substitutes the
    /// remembered name for any adapter that has since lost its driver. This is what keeps
    /// a driver predicate matching after the driver it installs disappears.
    ///
    /// Driver version is deliberately not restored - the driver really is gone, and the
    /// package's own installcheck must still see that.
    /// </summary>
    private void RestoreLastKnownGpuNames(List<GpuAdapter> adapters)
    {
        var cache = LoadGpuCache();
        var changed = false;

        foreach (var adapter in adapters.Where(a => !a.NameIsGeneric && a.PciId.Length > 0))
        {
            if (cache.TryGetValue(adapter.PciId, out var known) && known.Name == adapter.Name)
                continue;

            cache[adapter.PciId] = new GpuCacheEntry
            {
                Name = adapter.Name,
                VramBytes = adapter.VramBytes,
                LastSeenUtc = DateTime.UtcNow
            };
            changed = true;
        }

        foreach (var adapter in adapters.Where(a => a.NameIsGeneric && a.PciId.Length > 0))
        {
            if (!cache.TryGetValue(adapter.PciId, out var known) || known.Name.Length == 0)
                continue;

            _logger.LogInformation(
                "GPU {PciId} has no vendor driver bound (Windows reports '{Generic}'); using last known model '{Known}' so driver predicates still match",
                adapter.PciId, adapter.Name, known.Name);

            adapter.Name = known.Name;
            adapter.RestoredFromCache = true;
            if (adapter.VramBytes == 0)
                adapter.VramBytes = known.VramBytes;
        }

        if (changed)
            SaveGpuCache(cache);
    }

    private Dictionary<string, GpuCacheEntry> LoadGpuCache()
    {
        try
        {
            if (!File.Exists(CimianPaths.GpuFactsCache))
                return new Dictionary<string, GpuCacheEntry>(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(CimianPaths.GpuFactsCache);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, GpuCacheEntry>>(json);
            return loaded == null
                ? new Dictionary<string, GpuCacheEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GpuCacheEntry>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read GPU fact cache {Path}; starting a fresh one",
                CimianPaths.GpuFactsCache);
            return new Dictionary<string, GpuCacheEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveGpuCache(Dictionary<string, GpuCacheEntry> cache)
    {
        try
        {
            Directory.CreateDirectory(CimianPaths.FactsDir);
            File.WriteAllText(
                CimianPaths.GpuFactsCache,
                JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            // Non-fatal: without the cache the collector simply loses driver-less recovery.
            _logger.LogWarning(ex, "Could not write GPU fact cache {Path}", CimianPaths.GpuFactsCache);
        }
    }

    /// <summary>
    /// Determines if a GPU is discrete (dedicated) rather than integrated.
    /// Borrowed from ReportMate HardwareModuleProcessor.IsDiscreteGpu()
    /// </summary>
    private static bool IsDiscreteGpu(string name)
    {
        var upper = name.ToUpperInvariant();

        // NVIDIA GPUs are always discrete
        if (upper.Contains("NVIDIA") || upper.Contains("GEFORCE") ||
            upper.Contains("QUADRO") || upper.Contains("RTX") ||
            upper.Contains("GTX"))
            return true;

        // AMD Radeon discrete GPUs (RX, PRO, XT series)
        if (upper.Contains("RADEON") &&
            (upper.Contains("RX") || upper.Contains("PRO") || upper.Contains("XT")))
            return true;

        return false;
    }

    /// <summary>
    /// Rounds VRAM bytes to the nearest common GPU memory size in GB.
    /// </summary>
    private static long RoundToCommonVramSize(long bytes)
    {
        if (bytes <= 0) return 0;
        double gb = bytes / (1024.0 * 1024.0 * 1024.0);
        int[] commonSizes = { 1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 32, 48, 64 };
        return commonSizes.OrderBy(s => Math.Abs(s - gb)).First();
    }

    private static long ToInt64(object? value)
    {
        try
        {
            return value == null ? 0 : Convert.ToInt64(value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }

    /// <summary>
    /// One display adapter for the duration of a collection run, merged from
    /// Win32_VideoController (driver-supplied detail) and Win32_PnPEntity (hardware identity).
    /// </summary>
    private sealed class GpuAdapter
    {
        /// <summary>Full PNP instance ID, used to match the two WMI views to each other.</summary>
        public string InstanceId { get; set; } = "";

        /// <summary>Stable hardware identity, e.g. "PCI\VEN_10DE&amp;DEV_24B0".</summary>
        public string PciId { get; set; } = "";

        public string Name { get; set; } = "";
        public string DriverVersion { get; set; } = "";
        public long VramBytes { get; set; }

        /// <summary>True when Name came from Windows' fallback rather than a vendor driver.</summary>
        public bool NameIsGeneric { get; set; }

        /// <summary>True once Name has been replaced with a previously observed model name.</summary>
        public bool RestoredFromCache { get; set; }

        /// <summary>Device Manager status; 28 means no driver is installed.</summary>
        public int ConfigManagerErrorCode { get; set; }

        public bool DriverMissing => NameIsGeneric || ConfigManagerErrorCode != 0;
    }

    /// <summary>One remembered adapter, persisted to <see cref="CimianPaths.GpuFactsCache"/>.</summary>
    private sealed class GpuCacheEntry
    {
        public string Name { get; set; } = "";
        public long VramBytes { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }

    // ==========================================================================
    // NPU Collection
    // ==========================================================================

    private void CollectNpuInfo(SystemFacts facts)
    {
        try
        {
            // Query PnP devices for NPU indicators
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%NPU%' OR Name LIKE '%Neural%' OR Name LIKE '%Hexagon%'");

            foreach (ManagementObject mo in searcher.Get())
            {
                var name = mo["Name"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(name) || !IsValidNpuDevice(name))
                    continue;

                facts.NpuAvailable = true;
                facts.NpuName = CleanNpuName(name);
                _logger.LogDebug("NPU detected: {Name}", facts.NpuName);
                return;
            }

            _logger.LogDebug("No NPU detected");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect NPU info");
        }
    }

    /// <summary>
    /// Filters out false positives from PnP device enumeration.
    /// Borrowed from ReportMate HardwareModuleProcessor.IsValidNpuDevice()
    /// </summary>
    private static bool IsValidNpuDevice(string deviceName)
    {
        var upper = deviceName.ToUpperInvariant();

        // Exclude common false positives
        if (upper.Contains("USB") || upper.Contains("BLUETOOTH") || upper.Contains("AUDIO") ||
            upper.Contains("KEYBOARD") || upper.Contains("MOUSE") || upper.Contains("CAMERA") ||
            upper.Contains("WEBCAM") || upper.Contains("SENSOR") || upper.Contains("PASSPORT") ||
            upper.Contains("POWER ENGINE") || upper.Contains("MANAGEMENT ENGINE") ||
            upper.Contains("AIROHA") || upper.Contains("WIRELESS") || upper.Contains("ETHERNET") ||
            upper.Contains("GNA") || upper.Contains("AI BOOST"))
            return false;

        // Must contain actual NPU terms
        return upper.Contains("NPU") || upper.Contains("NEURAL") ||
               upper.Contains("HEXAGON") || upper.Contains("TENSOR") ||
               upper.Contains("AI ACCELERATOR") || upper.Contains("AI PROCESSOR");
    }

    /// <summary>
    /// Cleans NPU names to readable format.
    /// Borrowed from ReportMate HardwareModuleProcessor.CleanNpuName()
    /// </summary>
    private static string CleanNpuName(string name)
    {
        // Remove INF file prefixes
        var match = global::System.Text.RegularExpressions.Regex.Match(name, @"^@[^;]+;\s*(.+)$");
        if (match.Success)
            name = match.Groups[1].Value;

        name = name.Replace("(R)", "").Replace("(TM)", "").Replace("®", "").Replace("™", "").Trim();

        if (name.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) &&
            name.Contains("Hexagon", StringComparison.OrdinalIgnoreCase))
            return "Qualcomm Hexagon NPU";
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
            name.Contains("NPU", StringComparison.OrdinalIgnoreCase))
            return "Intel NPU";
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) &&
            name.Contains("NPU", StringComparison.OrdinalIgnoreCase))
            return "AMD NPU";

        return name;
    }

    // ==========================================================================
    // RAM Collection (detailed type detection)
    // ==========================================================================

    private void CollectRamInfo(SystemFacts facts)
    {
        try
        {
            // Calculate total GB from already-collected TotalMemoryBytes
            if (facts.TotalMemoryBytes > 0)
            {
                facts.RamTotalGb = RoundToCommonRamSize(facts.TotalMemoryBytes);
            }

            // Query memory modules for type information
            using var searcher = new ManagementObjectSearcher(
                "SELECT SMBIOSMemoryType, Speed FROM Win32_PhysicalMemory");

            foreach (ManagementObject mo in searcher.Get())
            {
                var smbiosType = Convert.ToInt32(mo["SMBIOSMemoryType"] ?? 0);
                var speed = Convert.ToInt32(mo["Speed"] ?? 0);

                var ramType = MapMemoryType(smbiosType);
                if (ramType == "Unknown" && speed > 0)
                    ramType = InferMemoryTypeFromSpeed(speed);

                if (ramType != "Unknown")
                {
                    facts.RamType = ramType;
                    _logger.LogDebug("RAM type detected: {Type} (SMBIOS={Smbios}, Speed={Speed}MHz)",
                        ramType, smbiosType, speed);
                    return; // All modules are typically the same type
                }
            }

            _logger.LogDebug("RAM type could not be determined");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect RAM info");
        }
    }

    /// <summary>
    /// Rounds total RAM bytes to the nearest common size in GB.
    /// </summary>
    private static int RoundToCommonRamSize(long bytes)
    {
        double gb = bytes / (1024.0 * 1024.0 * 1024.0);
        int[] commonSizes = { 2, 4, 8, 16, 32, 64, 128, 256, 512 };
        return commonSizes.OrderBy(s => Math.Abs(s - gb)).First();
    }

    /// <summary>
    /// Maps SMBIOS memory type codes to readable names.
    /// Borrowed from ReportMate HardwareModuleProcessor.MapMemoryType()
    /// </summary>
    private static string MapMemoryType(int smbiosType)
    {
        return smbiosType switch
        {
            20 => "DDR",
            21 => "DDR2",
            24 => "DDR3",
            26 => "DDR4",
            34 => "DDR5",
            30 => "LPDDR4",
            35 => "LPDDR5",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Infers memory type from speed when SMBIOS type is unavailable.
    /// Borrowed from ReportMate HardwareModuleProcessor.InferMemoryTypeFromSpeed()
    /// </summary>
    private static string InferMemoryTypeFromSpeed(int speedMHz)
    {
        if (speedMHz >= 4400) return "DDR5";
        if (speedMHz >= 2133) return "DDR4";
        if (speedMHz >= 800) return "DDR3";
        if (speedMHz >= 400) return "DDR2";
        return "Unknown";
    }

    // ==========================================================================
    // Storage Collection
    // ==========================================================================

    private void CollectStorageInfo(SystemFacts facts)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Model, MediaType, InterfaceType, Size FROM Win32_DiskDrive");

            long largestCapacity = 0;

            foreach (ManagementObject mo in searcher.Get())
            {
                var model = mo["Model"]?.ToString() ?? "";
                var mediaType = mo["MediaType"]?.ToString() ?? "";
                var interfaceType = mo["InterfaceType"]?.ToString() ?? "";
                var size = Convert.ToInt64(mo["Size"] ?? 0);

                // Skip tiny/virtual drives
                if (size < 1_000_000_000) continue;

                var driveType = DetermineStorageType(model, mediaType, interfaceType);
                var capacityGb = (long)Math.Round(size / (1024.0 * 1024.0 * 1024.0));

                // Use largest internal drive as primary
                if (size > largestCapacity)
                {
                    largestCapacity = size;
                    facts.StorageType = driveType;
                    facts.StorageCapacityGb = capacityGb;
                }
            }

            _logger.LogDebug("Storage info collected: type={Type}, capacity={Capacity}GB",
                facts.StorageType, facts.StorageCapacityGb);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect storage info");
        }
    }

    /// <summary>
    /// Determines storage type from drive model name, media type, and interface.
    /// Borrowed from ReportMate HardwareModuleProcessor.DetermineStorageType()
    /// </summary>
    private static string DetermineStorageType(string model, string mediaType, string interfaceType)
    {
        var lower = (model + " " + mediaType + " " + interfaceType).ToLowerInvariant();

        if (lower.Contains("nvme") || lower.Contains("pcie"))
            return "NVMe";
        if (lower.Contains("ssd") || lower.Contains("solid state"))
            return "SSD";
        if (lower.Contains("hdd") || lower.Contains("mechanical"))
            return "HDD";

        // Common SSD manufacturer keywords
        if (lower.Contains("samsung") || lower.Contains("crucial") || lower.Contains("kingston") ||
            lower.Contains("sandisk") || lower.Contains("micron") || lower.Contains("sk hynix") ||
            lower.Contains("intel") || lower.Contains("wd") || lower.Contains("western digital"))
            return "SSD";

        return "SSD"; // Default to SSD for modern systems
    }

    // ==========================================================================
    // CPU Helpers
    // ==========================================================================

    /// <summary>
    /// Cleans processor names by removing trademark symbols, speed suffixes, and manufacturer prefixes.
    /// Borrowed from ReportMate HardwareModuleProcessor.CleanProcessorName()
    /// </summary>
    private static string CleanProcessorName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var cleaned = name
            .Replace("(R)", "").Replace("(TM)", "")
            .Replace("®", "").Replace("™", "").Trim();

        // Remove generation prefix + manufacturer (e.g., "13th Gen Intel")
        cleaned = global::System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"^\d+(st|nd|rd|th)\s+Gen\s+Intel\s+", "",
            global::System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        // Remove standalone manufacturer prefixes
        if (cleaned.StartsWith("Intel ", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[6..].Trim();
        else if (cleaned.StartsWith("AMD ", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[4..].Trim();

        // Remove "CPU @ X.XXGHz" suffix
        cleaned = global::System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"\s*CPU\s*@\s*[\d.]+\s*GHz\s*$", "",
            global::System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        // Remove standalone "CPU" at end
        cleaned = global::System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"\s+CPU\s*$", "",
            global::System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        // Map known Snapdragon chip IDs to marketing names
        if (cleaned.Contains("X1E80100", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("X1E84100", StringComparison.OrdinalIgnoreCase))
            return "Snapdragon X Elite";
        if (cleaned.Contains("X1E78100", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("X1E68100", StringComparison.OrdinalIgnoreCase))
            return "Snapdragon X Plus";

        return cleaned;
    }

    /// <summary>
    /// Maps WMI Manufacturer string to a clean manufacturer name.
    /// Falls back to parsing the CPU brand string for known manufacturers.
    /// </summary>
    private static string MapCpuManufacturer(string manufacturer, string cpuName)
    {
        var mfg = manufacturer.Trim();
        if (mfg.Equals("GenuineIntel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        if (mfg.Equals("AuthenticAMD", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (mfg.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase)) return "Qualcomm";
        if (mfg.Contains("ARM", StringComparison.OrdinalIgnoreCase)) return "ARM";

        // Fallback: check CPU name
        var upper = cpuName.ToUpperInvariant();
        if (upper.Contains("INTEL")) return "Intel";
        if (upper.Contains("AMD")) return "AMD";
        if (upper.Contains("QUALCOMM") || upper.Contains("SNAPDRAGON")) return "Qualcomm";

        return mfg;
    }
}
