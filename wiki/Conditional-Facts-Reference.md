# Conditional Facts Reference

This is the complete set of facts a `condition` expression can test. Anything not listed
here resolves to nothing, which stringifies to the empty string — so a mistyped fact name
produces a condition that quietly matches or quietly does not, rather than an error.

Fact names are case-insensitive. For the expression syntax and the operators, see
[Conditional-Items](Conditional-Items).

Facts are collected once per run, immediately before conditions are evaluated. If
collection fails, the run continues against a minimal set — `hostname`, `arch`,
`os_version`, `os_vers_major`, `os_vers_minor`, `os_build_number`, `catalogs`, plus
`machine_type` fixed at `desktop` and `machine_model` fixed at `Unknown`. Every other fact
is then empty or zero.

## Core

| Fact | Type | Example | Source |
|---|---|---|---|
| `hostname` | string | `WS-0001` | The computer's machine name. |
| `arch` | string | `x64` | OS architecture, normalised to `x64`, `x86`, `ARM64` or `ARM`. |
| `architecture` | string | `x64` | Alias of `arch`. |
| `os_version` | string | `10.0.26100` | `Win32_OperatingSystem.Version`, falling back to the major.minor.build reported by the runtime. |
| `os_vers_major` | int | `10` | Major version reported by the runtime. Note this is the kernel major, so Windows 11 reports `10` here — use `os_build_number` to separate the two. |
| `os_vers_minor` | int | `0` | Minor version reported by the runtime. |
| `os_build_number` | int | `26100` | Build number reported by the runtime. |
| `domain` | string | `contoso` | `Win32_ComputerSystem.Domain`. On a device that is not directory-joined this holds the workgroup name. |
| `username` | string | `svc-install` | The account the client is running as, not the interactive user. |
| `machine_type` | string | `desktop` | One of `laptop`, `desktop`, `virtual`, `server`. Virtualisation is detected from the system manufacturer and model; laptop and server from the chassis type, with the presence of a battery as a secondary laptop indicator. |
| `machine_model` | string | `OptiPlex 7090` | `Win32_ComputerSystem.Model`. On some vendors this is a product code rather than a readable name — see `model_version`. |
| `model_version` | string | `ThinkCentre M75q Gen 2` | `Win32_ComputerSystemProduct.Version`. Empty on vendors that do not populate it. |
| `joined_type` | string | `hybrid` | One of `workgroup`, `domain`, `entra`, `hybrid`. Derived from `Win32_ComputerSystem` combined with the cloud domain-join registry state. |
| `battery_state` | string | `connected` | One of `connected`, `disconnected`, `unknown`, from `Win32_Battery`. |
| `date` | string | `2026-01-15` | Today's date in `yyyy-MM-dd`, in the device's **local** time. |
| `catalogs` | list of strings | `[Production]` | The catalog names contributed by the whole manifest tree. Use with `ANY`, or with `==`, which is a membership test against a list. |

## GPU

`gpu_names`, `gpu_pci_ids` and `gpu_vendors` are lists covering every adapter, and each has
a singular alias that returns the same list.

| Fact | Type | Example | Source |
|---|---|---|---|
| `gpu_names` / `gpu_name` | list of strings | `[Example GPU 4000]` | `Win32_VideoController.Name`. Windows only reports a real model name while the vendor driver is bound; the client caches each adapter's name against its PCI hardware ID and re-supplies it when the driver disappears, so a model-name condition keeps matching on a device Cimian has previously seen with a working driver. |
| `gpu_driver_version` | string | `31.0.15.5222` | `Win32_VideoController.DriverVersion` for the primary adapter. Empty when no vendor driver is bound, and deliberately **not** restored from the cache, so a driver package's own detection still sees that nothing is installed. |
| `gpu_vram_gb` | long | `8` | VRAM of the primary adapter, in GB. |
| `gpu_pci_ids` / `gpu_pci_id` | list of strings | `[PCI\VEN_10DE&DEV_24B0]` | PCI hardware IDs from the PnP enumerator. Present with no driver installed, which makes this the reliable way to target a device that needs its driver reinstalled. |
| `gpu_vendors` / `gpu_vendor` | list of strings | `[NVIDIA]` | Vendors resolved from the PCI vendor ID. Also driver-independent. |
| `gpu_driver_missing` | bool | `true` | True when at least one adapter has no vendor driver bound. |

## CPU

| Fact | Type | Example | Source |
|---|---|---|---|
| `cpu_name` | string | `Core i9-13900K` | `Win32_Processor.Name`, cleaned of vendor branding and trailing marketing text. |
| `cpu_manufacturer` | string | `Intel` | Normalised to `Intel`, `AMD`, `Qualcomm` or `ARM`. |
| `cpu_cores` | int | `8` | `Win32_Processor.NumberOfCores`. |
| `cpu_logical_cores` | int | `16` | `Win32_Processor.NumberOfLogicalProcessors`. |

## NPU

| Fact | Type | Example | Source |
|---|---|---|---|
| `npu_name` | string | `Example Hexagon NPU` | PnP device name, matched on NPU/neural-processor naming. Empty when none is present. |
| `npu_available` | bool | `true` | True when such a device was found. |

## Memory and storage

| Fact | Type | Example | Source |
|---|---|---|---|
| `ram_total_gb` | int | `32` | Total physical memory, rounded to a common size (8, 16, 32, 64, 128). Use this for thresholds, not `totalmemorybytes`. |
| `ram_type` | string | `DDR5` | From `Win32_PhysicalMemory`. One of `DDR3`, `DDR4`, `DDR5`, `LPDDR4`, `LPDDR5`. Empty when the firmware does not report a recognised type. |
| `storage_type` | string | `NVMe` | Primary drive media, from `Win32_DiskDrive`. One of `NVMe`, `SSD`, `HDD`. |
| `storage_capacity_gb` | long | `512` | Primary drive capacity in GB. |

## Legacy spellings

These names predate the underscored convention and are kept working. They are all
lowercase with no separators, and the spelling matters — `is_enrolled` is not a fact.

| Fact | Type | Example | Source |
|---|---|---|---|
| `operatingsystem` | string | `Microsoft Windows 11 Enterprise` | `Win32_OperatingSystem.Caption`. |
| `operatingsystemversion` | string | `10.0.26100` | Same value as `os_version`. |
| `operatingsystembuild` | string | `26100` | Same value as `os_build_number`, as a string rather than an int, so comparisons against it may fall back to string ordering. Prefer `os_build_number`. |
| `isdomainjoined` | bool | `true` | `Win32_ComputerSystem.PartOfDomain`. |
| `isenrolled` | bool | `true` | True when an MDM enrollment is present in the enrollment registry. |
| `uptimeseconds` | long | `86400` | Seconds since last boot, from `Win32_OperatingSystem.LastBootUpTime`. |
| `totalmemorybytes` | long | `34359738368` | `Win32_ComputerSystem.TotalPhysicalMemory`. |
| `availablememorybytes` | long | `9663676416` | `Win32_OperatingSystem.FreePhysicalMemory`. Varies run to run — not a sound basis for a deployment decision. |

## Facts that do not exist

Manifests written against other systems, and some older examples, reference names that
have never been part of this fact map. Each resolves to nothing:

`serial_number`, `device_id`, `build_number`, `enrolled_usage`, `enrolled_area`.

Use `os_build_number` for the build number. For anything site-specific, supply it as a
custom fact.

## Custom facts

Custom facts are supported, and they are supplied by scripts on the device rather than by
configuration.

At the start of a run the client scans `%ProgramData%\ManagedInstalls\conditions\` — the
directory only, not subdirectories — and runs every `.ps1`, `.bat`, `.cmd` and `.exe` it
finds, in file-name order. Each script's standard output is read as `key=value` lines:
the text before the first `=` becomes the fact name, lowercased and trimmed, and the rest
of the line becomes its value, trimmed. Values are always strings, so a numeric comparison
against a custom fact works only when the text parses as a number.

The rules to design around:

- A script gets **30 seconds**. It is killed on timeout and contributes nothing.
- A **non-zero exit code discards all of that script's output**, including lines already
  printed. Exit 0 or supply nothing.
- Lines without an `=`, and lines beginning with `=`, are ignored.
- Later scripts overwrite earlier ones on a name collision, so file-name order is your
  precedence order.
- A custom fact does not override a built-in one. Built-ins are matched first; the custom
  set is only consulted for names that are not already facts.

A script placed at `%ProgramData%\ManagedInstalls\conditions\10-site.ps1`:

```powershell
$room = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Example\Site' -Name 'Room' -ErrorAction SilentlyContinue).Room
if ($room) { "site_room=$room" }
"site_tier=standard"
exit 0
```

Those names are then available like any other fact:

```yaml
conditional_items:
  - condition: site_tier == "standard" AND site_room BEGINSWITH "LAB"
    managed_installs:
      - ExampleLabApp
```

Two further fallback dictionaries — process environment variables and gathered registry
values — are consulted after custom facts when a name is unrecognised. **Nothing in the
client populates either of them**, so in practice the conditions directory is the only way
to add a fact.

## See also

- [Conditional-Items](Conditional-Items)
- [Manifests](Manifests)
- [Client-Configuration](Client-Configuration)
- [managedsoftwareupdate](managedsoftwareupdate)
- [Troubleshooting](Troubleshooting)
