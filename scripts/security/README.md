# ManagedInstalls hardening validation

Run these checks from an interactive Windows session with Windows Sandbox enabled. They install and uninstall Cimian, create a standard local user, and plant harmless content inside the disposable Sandbox. The guest script refuses to run unless the account is Windows Sandbox's WDAGUtilityAccount.

The candidate must be a signed x64 MSI. Use an unpatched released x64 MSI as the baseline. The host wrapper verifies the candidate signature, copies both packages into a fresh input directory, records their hashes, and exports the public signing trust chain for the guest. It never exports private keys.

Start the validation from the repository root:

```powershell
.\scripts\security\Invoke-HardeningSandbox.ps1 -CandidateMsi C:\Packages\candidate.msi -BaselineMsi C:\Packages\baseline.msi -OutputDirectory C:\Validation\CimianHardening
```

The input mapping is read-only. Networking and clipboard redirection are disabled. Only the output mapping is writable. The guest shuts down after recording its result.

Read the result after the Sandbox exits:

```powershell
Get-Content C:\Validation\CimianHardening\output\result.json
```

A successful result covers fresh installation, upgrade from the baseline, repeatable permissions repair, preservation of administrator configuration, quarantine/removal of user-planted scripts and cached content, denied standard-user access to protected paths, and Self Service requests from standard and elevated users. MSI logs and a transcript accompany the result. This is an installation smoke test; run the full C# suite for request ordering, cancellation, intake limits, watcher argument validation and the other trust checks.

The pinned legacy builder uses VBScript custom actions. A stock Sandbox image without VBScript fails before the application runs, with MSI error 2738. Keep that result as a compatibility failure. To test the security patch on a VBScript-present guest, explicitly add `-ProvisionLegacyVBScript` and choose a new output directory. The wrapper copies the host's signed Windows VBScript runtime and its engine registrations into the read-only fixture; the guest registers them only inside the disposable Sandbox. Results record `LegacyVBScriptProvisioned: true`. This does not establish compatibility with a stock image lacking VBScript; the separate VBScript-free package-builder migration addresses that requirement.

With the VBScript-free builder pinned, validate a stock fresh install and an upgrade after the baseline engine has been removed:

```powershell
.\scripts\security\Invoke-HardeningSandbox.ps1 -CandidateMsi C:\Packages\candidate.msi -BaselineMsi C:\Packages\baseline.msi -ProvisionLegacyVBScript -RequireVBScriptFree -OutputDirectory C:\Validation\CimianVBScriptFree
```

In this mode the candidate installs and uninstalls before the legacy engine fixture is enabled. The fixture is then enabled only for the old baseline installation. Windows Installer processes are stopped and the engine DLLs, CLSIDs and ProgIDs removed before upgrading to the candidate. Assertions verify both DLLs are absent and a script-host probe cannot use VBScript before fresh installation and before upgrade. The result records `RequireVBScriptFree: true`; all earlier security checks still run. This validates effective engine removal inside the Sandbox rather than Windows Feature on Demand servicing.
