# CimianTools MSI / GH Actions Release Pipeline — Learnings from 2026-06-05 Fleet Sweep

Context: pushed CimianTools 2026.06.05.0221 to ~365 Windows devices via the new `cimian-bootstrap-mgmt` pipeline. SSH-swept the fleet, ran a fallback manual install on the 35 stale-but-reachable devices. 27/35 installed cleanly first try; 8 failed; root-cause analysis revealed gaps the MSI itself + the release pipeline should address.

## Failure breakdown observed in the wild

| Failure mode | Count | Root cause |
|---|---|---|
| SCP fail — disk full | 3 | Workstations with C:\ at 0 bytes free |
| SCP fail — network/host instability | 1 | Slow / dropping links, also partial MSI in Temp |
| msiexec `1618` | 3 | Another msiexec session held the global mutex; ours bailed instantly |
| msiexec `112` | 1 | Insufficient disk space mid-install |
| `1402` registry rollback noise | 2 | Transient — concurrent install had locked `HKLM\...\Installer\Rollback`. Cleared on retry. |

## Concrete MSI / pipeline upgrades

### 1. Pre-install launch conditions (in WiX)
The MSI silently proceeds and burns ~290 MB of bandwidth + SCP time before failing if disk is full. Bake hard guards into WiX:

```xml
<!-- Require >= 1.5 GB free on system drive before allowing install -->
<Condition Message="Insufficient disk space on [WindowsVolume]. CimianTools requires at least 1.5 GB free.">
  <![CDATA[Installed OR (WindowsVolume_FreeSpace >= 1500)]]>
</Condition>

<!-- Block if reboot is pending -->
<Property Id="REBOOTPENDING">
  <RegistrySearch Id="RebootRequired" Root="HKLM"
    Key="SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"
    Type="raw" />
</Property>
<Condition Message="A pending reboot is required before installing CimianTools. Please reboot first.">
  <![CDATA[Installed OR NOT REBOOTPENDING]]>
</Condition>
```

### 2. Mutex backoff inside Cimian (not WiX)
1618 = `ERROR_INSTALL_ALREADY_RUNNING`. WiX can't fix this — but `managedsoftwareupdate` invoking msiexec **can** retry with backoff:

- Detect 1618 explicitly. Sleep 30–60s. Retry up to 3 times.
- After final 1618, log which other msiexec is holding the mutex (`tasklist /fi "imagename eq msiexec.exe"`) so we can correlate with other Cimian-triggered installs.
- Bonus: Cimian itself should serialise its own msiexec calls so two Cimian-managed packages installed in the same cycle don't collide.

### 3. Cimian hourly task should self-heal — not return result=1 every hour
Several devices showed `TaskResult=1` hour after hour without recovery. The task should:

- Capture msiexec exit code, log it, and surface in ReportMate hardware/status.
- On 1618: schedule itself again in 10–15 min instead of waiting a full hour.
- On 112/1603/1622: trigger a disk-space + permissions probe and emit a ReportMate event so we see it.
- On 1402: attempt a targeted ACL reset of `HKLM\Software\Microsoft\Windows\CurrentVersion\Installer\Rollback` *before* retrying, using a signed helper in the Cimian SYSTEM context (privileges available, unlike admin SSH).

### 4. MSI verbose log retention + upload
`/L*v C:\Windows\Temp\CimianTools-install.log` is good. Make it:
- **Rotate** (keep last 3 attempts, not overwrite each time).
- **Auto-include in ReportMate event** on non-zero exit. Right now this exercise required SSH to fetch the tail — that should be a ReportMate field.

### 5. Detect & recover partial MSI deliveries
Partial MSI files (e.g. 284 MB out of 302 MB after SCP timeout, or in the wild: Cimian's own download interrupted) were left in `C:\Windows\Temp\CimianTools-update.msi`. Downstream installs then fail strangely.
- The pipeline should ship a known-good `SHA256SUMS.txt` alongside the MSI artifacts.
- Cimian's downloader should verify the digest before calling msiexec. On mismatch, delete & redownload.
- GH Actions release step: add `sha256sum *.msi > SHA256SUMS.txt` and upload it as part of the release.

### 6. GH Actions release: dual-arch + signing verification
We saw `2026.06.05.0221` on both `x64` and `arm64` SKUs and the runtime arch detection (`$env:PROCESSOR_ARCHITECTURE`) drove which MSI to install. Pipeline should:
- Emit both `CimianTools-x64-<ver>.msi` and `CimianTools-arm64-<ver>.msi` as separate release assets (already happening — good).
- Run `signtool verify /pa /v <msi>` in the workflow and fail the build if either MSI is unsigned. Per our project rule: this system cannot run unsigned binaries.
- Output a release-notes block with the SHA256 of each MSI for audit.

### 7. WiX: shrink the MSI
At 302 MB (x64) and 290 MB (arm64), SCP to a slow link takes 30–60s and is the #1 driver of "TIMEOUT" results when sweeping the fleet. Audit what's inside:
- Are there `.pdb` symbol files shipping in release? Strip them.
- Are we bundling the Go toolchain or vendor deps inadvertently?
- If unavoidable, consider a `.cab`-stripped MSI + side-by-side compressed payload that Cimian's downloader unpacks.

### 8. Better launch-condition messages for ReportMate to surface
WiX `Condition Message` strings go to the MSI log and Event Viewer but not to a structured channel. Wrap msiexec invocation in a Cimian helper that:
- Parses the log for `Condition '...' evaluated to FALSE`.
- Sends the human-readable message to ReportMate as a structured event.

### 9. Pipeline-level: gate the bootstrap-mgmt rollout on a smoke test
Currently the pipeline pushes the new `management.json` and the new MSI catalog reference to the fleet in one shot. Add a smoke stage:
- Pick 2–3 representative test devices (lab + render + staff).
- After publishing the MSI to the blob, run the Cimian hourly on those devices, wait, and confirm version bump + zero `1618/112/1402/1603`.
- Only then promote to full fleet.

This is the same `Native-Full` cutover pattern already in the pipeline but with an explicit gate.

### 10. Catch the `RENDER-NODE-02` class of failure (no Cimian at all)
One device had Cimian uninstalled entirely. The bootstrap should:
- On every Cimian hourly run, check if `C:\Program Files\Cimian\managedsoftwareupdate.exe` exists.
- If missing, ReportMate emit a `cimian_missing` event so we see it in the fleet view.
- Optionally trigger CimianBootstrap-driven reinstall.

## Quick wins (do these first)
1. **SHA256SUMS.txt in the release** — trivial GH Actions step, biggest robustness gain.
2. **Disk-space launch condition in WiX** — one-line addition, prevents msi=112.
3. **1618 retry-with-backoff in Cimian** — eliminates ~half the failures we saw.
4. **Self-heal task on result=1** — devices that fail at hour H currently fail at H+1, H+2, ... forever.
