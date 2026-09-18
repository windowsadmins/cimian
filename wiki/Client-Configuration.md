# Client Configuration

Every Cimian client reads one configuration file. This page is the complete
reference for that file: where it lives, what format it uses, every key it
accepts, and what overrides it. If you have configured Munki, this is Cimian's
equivalent of `ManagedInstalls.plist`.

## Where the configuration lives

The client reads `%ProgramData%\ManagedInstalls\Config.yaml`. On a default
Windows installation that is `C:\ProgramData\ManagedInstalls\Config.yaml`, but
the path is resolved from the machine's `%ProgramData%` location rather than
hard-coded, so a relocated `ProgramData` moves the file with it.

You can point `managedsoftwareupdate` at a different file for a single run:

```
managedsoftwareupdate --config C:\Temp\test-config.yaml --checkonly
```

No other tool honours `--config`; the alternate file applies only to that
invocation of `managedsoftwareupdate`.

If the file does not exist, the client runs with built-in defaults and says
nothing. If the file exists but cannot be parsed, the client prints
`Failed to load configuration from <path>` and then runs with built-in
defaults — a broken file does not stop the run, so a typo can silently send a
client to the wrong repository.

## Format

The file is YAML. **Keys are PascalCase** — `SoftwareRepoURL`,
`ClientIdentifier`, `CacheRetentionDays`. Cimian does not accept snake_case or
camelCase spellings of these keys.

Unrecognised keys are discarded without a warning. That is the single most
common configuration mistake: a misspelled or wrongly-cased key produces no
error, no log line, and no effect. After any edit, confirm the result with
`managedsoftwareupdate --show-config`.

`SoftwareRepoURL` must be an `http://` or `https://` URL. No other scheme is
supported — there is no `file://` and no UNC path support in the fetch path.

## Configuration keys

Defaults below are the values that apply when the key is absent from the file.

### Repository and identity

| Key | Type | Default | Effect |
|---|---|---|---|
| `SoftwareRepoURL` | string | none | Base URL of the served Cimian repository. Manifests are fetched from `<repo>/manifests/<name>.yaml`, catalogs from `<repo>/catalogs/<name>.yaml`, icons from `<repo>/icons/`, and packages from `<repo>/pkgs/<location>`. Required; must be `http` or `https`. |
| `ClientIdentifier` | string | machine name | Name of the primary manifest this device requests. See [Client Identifier Resolution](Client-Identifier-Resolution) for the full fallback chain. |
| `Catalogs` | list of string | `["Production"]` | Catalogs to consult. An empty or absent list means `Production`. Precedence between catalogs is highest-version-wins, not list order. |
| `ManifestsPath` | string | `%ProgramData%\ManagedInstalls\manifests` | Local directory for downloaded manifests. An explicitly blank value resets to the default. |
| `CatalogsPath` | string | `%ProgramData%\ManagedInstalls\catalogs` | Local directory for downloaded catalogs. An explicitly blank value resets to the default. |

### Scheduling and run behaviour

Cimian has no configurable run interval. Cadence is set by the scheduled tasks,
not by this file — see [How Cimian Runs](How-Cimian-Runs).

| Key | Type | Default | Effect |
|---|---|---|---|
| `NoPreflight` | bool | `false` | Never run the preflight script. |
| `NoPostflight` | bool | `false` | Never run the postflight script. |
| `PreflightFailureAction` | string | `continue` | What to do when preflight exits non-zero. `abort` ends the session as failed and skips everything after it, including postflight. `warn` and any other value log a warning and continue. |
| `SkipSelfService` | bool | `false` | Ignore the self-service manifest entirely, so user Install/Remove requests from Managed Software Center have no effect. |
| `AutoRemove` | bool | `false` | Uninstall packages that Cimian installed but that no longer appear in any manifest. Only items that are uninstallable are removed. |
| `UsageStaleUninstallEnabled` | bool | `true` | Master switch for removing software that a pkgsinfo has marked for unused-software removal. |
| `UsageStaleUninstallMinimumHistoryDays` | int (days) | `14` | Floor applied to each item's own `minimum_history_days`. An item cannot be removed for disuse with less history than this. |
| `UsageStaleUninstallMaxSourceStalenessDays` | int (days) | `7` | If the usage telemetry on the device is older than this, the unused-software pass is skipped for the whole run. |

### Cache and retention

| Key | Type | Default | Effect |
|---|---|---|---|
| `CachePath` | string | `%ProgramData%\ManagedInstalls\Cache` | Root of the download cache. An explicitly blank value resets to the default. |
| `CacheRetentionDays` | int (days) | `30` | Cached payloads whose last-write time is older than this are deleted at the start of each run, before downloading. `0` or a negative value disables pruning entirely. |

Cimian keeps only one retention window for the cache. There is no per-package
retention and no size cap. See [The Download Cache](The-Download-Cache).

### Logging and reporting

| Key | Type | Default | Effect |
|---|---|---|---|
| `LogLevel` | string | `INFO` | Recorded and reported, but it does not gate output. Console and file verbosity come from the `-v` flags on the command line. |
| `Verbose` | bool | `false` | Same: written by the `-v` flags and reported by `--show-config`, but not read to decide what is logged. |
| `Debug` | bool | `false` | Same. |

Log retention is a hard-coded 30-day window over the whole log tree and is not
configurable from this file. See [Logging](Logging).

### Installer behaviour

| Key | Type | Default | Effect |
|---|---|---|---|
| `InstallerTimeout` | int (seconds) | `900` | Per-installer timeout. Values below 60 are rejected by validation. |
| `ForceChocolatey` | bool | `false` | Route installs through Chocolatey rather than the normal installer dispatch. |
| `SbinInstallerPath` | string | none | Explicit path to the sbin installer executable. When unset, Cimian looks in `C:\Program Files\sbin\installer.exe` and then `C:\Program Files (x86)\sbin\installer.exe`. This key is not consulted by the self-update path, which always uses the first of those two literal paths. |
| `SbinInstallerTargetRoot` | string | `/` | Value passed to the sbin installer's `--target` argument. |
| `PkgRequireSignature` | bool | `false` | Refuse to install an unsigned `.pkg`. |

### Install-loop suppression

| Key | Type | Default | Effect |
|---|---|---|---|
| `LoopGuardEnabled` | bool | `true` | Master switch for install-loop suppression. When false, the client logs that suppression is off and installs a looping package on every run. |
| `LoopMaxTime` | int (days) | `7` | Upper bound on any single suppression window. |
| `LoopReprobeHours` | int (hours) | `24` | How long to wait before re-probing a package that has not converged. Capped at `LoopMaxTime`. |

Loop suppression is disabled outright during bootstrap runs, and on-demand and
recurring items are exempt from it. See
[Install Loop Prevention](Install-Loop-Prevention).

### Security and authentication

Cimian applies exactly one `Authorization` header, chosen by this precedence: a
DPAPI-protected Basic credential stored in the registry, then `AuthToken`, then
`AuthUser`/`AuthPassword`. Client certificates are independent and apply in
addition to whichever header is chosen.

| Key | Type | Default | Effect |
|---|---|---|---|
| `AuthToken` | string | none | Sent as `Authorization: Bearer <token>`. |
| `AuthUser` | string | none | Basic-auth user name. Stored in plain text in the file. |
| `AuthPassword` | string | none | Basic-auth password. Stored in plain text in the file. |
| `UseClientCertificate` | bool | `false` | Enable client-certificate (mTLS) authentication to the repository. |
| `ClientCertificatePath` | string | none | Path to a PFX/P12 file, or to a PEM certificate when used with `ClientKeyPath`. |
| `ClientCertificatePassword` | string | none | Password for the PFX/P12 file. |
| `ClientCertificateThumbprint` | string | none | Thumbprint to look up instead of a file. `LocalMachine\My` is searched first, then `CurrentUser\My`. |
| `ClientKeyPath` | string | none | Private key file to pair with a PEM `ClientCertificatePath`. |
| `SoftwareRepoCACertificate` | string | none | CA certificate used to validate the repository's TLS certificate. Chain validation is still performed against this root; it is not a blind-accept switch. |
| `UseClientCertificateCNAsClientIdentifier` | bool | `false` | Use the client certificate's common name as the primary manifest name, ahead of `ClientIdentifier`. |

The Basic credential in the registry is `HKLM\SOFTWARE\Cimian` value
`AuthHeader`, holding a Base64 blob protected with DPAPI at machine scope. It is
not settable from `Config.yaml` and not settable by MDM policy. See
[Securing The Repository](Securing-The-Repository).

Cimian does not implement proxy configuration, Windows Integrated authentication
(NTLM, Negotiate or Kerberos), shared-access-signature tokens, or arbitrary extra
request headers. Requests use whatever proxy behaviour the .NET HTTP stack
applies by default.

## Precedence: the registry policy override

Cimian reads a small policy key on every configuration load, and **policy always
wins over the file**. The order is:

1. Built-in defaults
2. `Config.yaml`
3. Blank-path normalisation, which resets an explicitly empty `CachePath`,
   `CatalogsPath` or `ManifestsPath` back to its default
4. `HKLM\SOFTWARE\Policies\Cimian`

The policy key is opened read-only. If it does not exist, nothing happens. If
reading it fails, the failure is logged at debug level and the file's values are
used. Policy is applied on every load path, including the paths taken when
`Config.yaml` is missing or fails to parse — so a client with no configuration
file at all still honours policy.

**Only four values are honoured, and no others:**

| Value name | Registry type | Applied when |
|---|---|---|
| `SoftwareRepoURL` | `REG_SZ` | Non-blank. Surrounding whitespace is trimmed. |
| `ClientIdentifier` | `REG_SZ` | Non-blank. Surrounding whitespace is trimmed. |
| `InstallerTimeout` | `REG_DWORD` or `REG_SZ` holding an integer | The value is 60 or greater. |
| `CacheRetentionDays` | `REG_DWORD` or `REG_SZ` holding an integer | The value is 0 or greater. |

Anything else you place under that key is ignored. There is no policy override
for catalogs, authentication, loop-guard settings, script behaviour or logging.

Cimian does not read `HKLM\SOFTWARE\Cimian\Config`. That key appears in older
documentation and has never been consulted by the client.

For delivering these values from an MDM, see
[Configuring Clients With Intune](Configuring-Clients-With-Intune).

## A minimal working configuration

Two keys are enough for a working client. Catalogs default to `Production` and
the manifest name defaults to the machine name.

```yaml
SoftwareRepoURL: https://cimian.example.com/repo
ClientIdentifier: WORKSTATION-01
```

## A commented walkthrough

The following file is a fuller example. Each key is explained below it rather
than inside it, so the block can be pasted as-is.

```yaml
SoftwareRepoURL: https://cimian.example.com/repo
ClientIdentifier: lab-standard
Catalogs:
  - Testing
  - Production
CacheRetentionDays: 14
InstallerTimeout: 3600
PreflightFailureAction: abort
AutoRemove: true
LoopReprobeHours: 12
AuthUser: cimian-client
AuthPassword: change-me
```

`SoftwareRepoURL` names the served repository. Every manifest, catalog, icon and
package URL is built from it.

`ClientIdentifier` requests `https://cimian.example.com/repo/manifests/lab-standard.yaml`
as the primary manifest. Setting it here means every device with this file shares
one manifest regardless of machine name.

`Catalogs` lists two catalogs. Both are downloaded and merged, and when an item
exists in both the higher version wins — listing `Testing` first does not make it
authoritative, its newer version does.

`CacheRetentionDays: 14` prunes cached payloads more aggressively than the
30-day default. Set this low on machines with small system drives; superseded
multi-gigabyte payloads are the usual cause of a full disk.

`InstallerTimeout: 3600` allows an hour per installer instead of fifteen minutes.
Raise this before deploying large suites; the run kills an installer that exceeds
it.

`PreflightFailureAction: abort` turns the preflight script into a gate. If it
exits non-zero, the session ends immediately as failed and nothing is installed.
The default, `continue`, only logs a warning.

`AutoRemove: true` uninstalls Cimian-installed software that has dropped out of
every manifest. Removal from a manifest becomes a removal from the device, so
enable this only when your manifests are the intended complete picture.

`LoopReprobeHours: 12` halves the wait before a suppressed package is retried.

`AuthUser` and `AuthPassword` are stored in clear text. Prefer the
DPAPI-protected registry credential or a client certificate on any device you do
not fully control.

## Keys that exist but do nothing

These keys deserialise without error and are reported by `--show-config`, but
nothing in the client acts on them.

| Key | Reality |
|---|---|
| `CheckOnly` | Check-only mode comes only from the `--checkonly` flag. Setting this in the file does not make runs check-only. |
| `LocalOnlyManifest` | Only the `--local-only-manifest` flag selects a local manifest. The file value is displayed and otherwise ignored. |
| `PostflightFailureAction` | A postflight script's non-zero exit always produces a warning and never changes the session result, whatever this is set to. |
| `UseCache` | Reported by `--cache-status`. The cache is used regardless. |
| `PreferSbinInstaller` | Not consulted anywhere. |
| `LogLevel`, `Verbose`, `Debug` | Written by the `-v` flags and reported, but not read to gate any output. |

Two other artefacts look like configuration and are not. A snake_case sample
config in the source tree's MSI build directory is not shipped, not referenced,
and uses key names that no version of the client reads. A second configuration
model in the source with snake_case keys such as `catalog_url`,
`max_concurrent_downloads` and `proxy` is not instantiated by anything. Neither
describes a real setting.

## Verifying what a client is using

`--show-config` prints the effective values after the file has been read and
policy applied:

```
managedsoftwareupdate --show-config
```

It does not print `CacheRetentionDays`. To confirm that value, along with the
cache path, size and oldest entry:

```
managedsoftwareupdate --cache-status
```

## See also

- [How Cimian Runs](How-Cimian-Runs)
- [Client Identifier Resolution](Client-Identifier-Resolution)
- [Configuring Clients With Intune](Configuring-Clients-With-Intune)
- [Securing The Repository](Securing-The-Repository)
- [The Download Cache](The-Download-Cache)
- [Install Loop Prevention](Install-Loop-Prevention)
- [Logging](Logging)
- [managedsoftwareupdate](managedsoftwareupdate)
