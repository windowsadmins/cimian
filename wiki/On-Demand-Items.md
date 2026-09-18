# On Demand Items

An on-demand item is a package that Cimian never treats as installed. Every run it is
reported as needing action, and every run it runs again. It exists for transient work —
provisioning steps, enrollment helpers, one-shot fixes — that must keep firing until
something outside Cimian changes and the item stops being handed to the client. This page
covers what `OnDemand` does, where it sits in the detection cascade, what it does to
reporting and to loop protection, and how it differs from an optional install.

## The key

`OnDemand` is a pkgsinfo key. Note the spelling: it is `OnDemand`, capitalised exactly like
that, **not** `on_demand`. Cimian's YAML reader does not apply a naming convention over
explicit aliases, so `on_demand: true` is an unrecognised key and is silently discarded —
the item behaves as a normal package and you get no warning.

```yaml
name: ExampleProvisioningStep
version: 1.0.0
catalogs:
  - Production
installer:
  type: nopkg
install_script: |
  $marker = 'C:\ProgramData\Example\provisioned.txt'
  if (Test-Path $marker) { exit 0 }
  New-Item -ItemType Directory -Force -Path (Split-Path $marker) | Out-Null
  Set-Content -Path $marker -Value (Get-Date -Format o)
  exit 0
OnDemand: true
unattended_install: true
```

`makepkginfo` sets it with a matching flag:

```
makepkginfo --OnDemand --nopkg --name ExampleProvisioningStep
```

## Where it sits in the detection cascade

The status check evaluates a fixed sequence of gates and the first definitive answer wins.
`OnDemand` is gate 0b — second only to the Cimian self-update guard, and **deliberately ahead
of `installcheck_script`**:

| Order | Gate |
|---|---|
| 0a | Cimian self-update guard |
| **0b** | **`OnDemand: true`** |
| 1 | `installcheck_script` |
| 1.5 | `version_script` |
| 2 | `installs[]` entries |
| 3 | `check.registry` |
| 4 | `check.file` |
| 5 | `check.script` |
| 6 | ManagedInstalls receipt |
| 7 | Fallback by installer type |

An on-demand item short-circuits at 0b with:

```
Status:           pending
Needs action:     yes
Reason:           OnDemand item — always (re)installed, never tracked as installed
Reason code:      on_demand
Detection method: none
```

Because the gate precedes every detection mechanism, **an on-demand item's
`installcheck_script`, `installs` array and `check` block are never consulted.** Leaving
detection metadata on an on-demand pkgsinfo is not harmful, but it is dead weight and it
misleads the next person to read the file. If you find yourself writing an installcheck for
an on-demand item, you want a normal item instead.

## No receipt is written

Cimian normally records a successful install in the registry at
`HKLM\SOFTWARE\ManagedInstalls\<Name>`. An on-demand item is the exception: after a
successful run the receipt is **deleted** rather than written. Any stale receipt left behind
by an older client that ignored the key is removed the same way.

This is what makes the behaviour stable rather than merely repeated. Without it, gate 6 of a
future client would find a receipt and start reporting the item as installed.

The practical consequence is that there is no local record of an on-demand item ever having
run. If the script needs to know whether it has run before, it must keep its own state —
a marker file, a registry value of its own, or the external condition it is waiting on.

## Loop protection

An on-demand item reinstalls on every run by design, which is exactly the pattern
[LoopGuard](Install-Loop-Prevention) exists to suppress. On-demand items are therefore
exempt at every level:

- LoopGuard's suppression check is bypassed for the item, with the bypass reason recorded as
  `OnDemand`.
- Install attempts are recorded as loop-exempt, so no counters accumulate and no suppression
  window can ever open.
- The post-install convergence probe — which normally flags a package that still reports
  "needs action" immediately after a successful install — skips on-demand items outright.
  They never converge, by definition.

That exemption is deliberate, and it is also the risk. **Nothing will ever stop an on-demand
item.** A script that fails every run fails silently and forever, once per hour, for as long
as the item is in the manifest. The safety net that catches this class of mistake on a normal
package is switched off.

## Reporting

An on-demand item appears in each session's item report like any other, with its outcome for
that run. What it never shows is a stable installed state: it is pending before the run and
carries that run's result afterwards, and the next run starts from pending again.

For fleet reporting this means an on-demand item is not a useful compliance signal. You
cannot ask "which machines have this?" because Cimian does not track that. Report on the
external state the script manipulates instead.

## When to use it

Use `OnDemand: true` when all three of these are true:

1. The action is idempotent and safe to run repeatedly.
2. There is no meaningful "installed" state to detect — or the state you care about lives
   somewhere Cimian cannot check.
3. Something other than the package's own success will eventually remove the item from the
   machine's manifest.

Point 3 is the one that gets skipped. An on-demand item with no exit route runs forever.
Give it one — normally a [conditional item](Conditional-Items) whose condition stops matching
once the work is done:

```yaml
name: WORKSTATION-01
catalogs:
  - Production
managed_installs:
  - Example App
conditional_items:
  - condition:
      key: "hostname"
      operator: "LIKE"
      value: "LAB-*"
    managed_installs:
      - ExampleProvisioningStep
```

Or scope it to a manifest that a machine is moved off once it is provisioned.

## Worked example

A lab machine has to register itself with an inventory service after imaging. The
registration can fail — the service is unreachable during the imaging window — and has to
keep retrying until it succeeds, but there is nothing on disk that reliably says "registered"
that Cimian could check.

```yaml
name: ExampleInventoryRegistration
display_name: Example Inventory Registration
version: 1.2.0
catalogs:
  - Production
category: provisioning
installer:
  type: nopkg
install_script: |
  $stamp = 'HKLM:\SOFTWARE\Example\Inventory'
  if ((Get-ItemProperty -Path $stamp -Name Registered -ErrorAction SilentlyContinue).Registered -eq 1) {
      exit 0
  }
  try {
      Invoke-RestMethod -Uri 'https://inventory.example.com/register' -Method Post `
          -Body (@{ hostname = $env:COMPUTERNAME } | ConvertTo-Json) `
          -ContentType 'application/json' -TimeoutSec 30 | Out-Null
  } catch {
      Write-Output "CIMIAN-WARNING: registration deferred: $($_.Exception.Message)"
      exit 0
  }
  New-Item -Path $stamp -Force | Out-Null
  Set-ItemProperty -Path $stamp -Name Registered -Value 1 -Type DWord
  exit 0
OnDemand: true
unattended_install: true
```

Points worth copying:

- The script is idempotent — it checks its own state first and exits immediately once the
  work is done. On-demand does not mean "do the work again", it means "ask again".
- A recoverable failure emits a `CIMIAN-WARNING:` line and exits 0. That marks the run a
  soft failure, surfaces the reason in reporting, and keeps the item retrying without
  reporting a hard install failure every hour.
- `unattended_install: true` lets it run in `auto` mode while somebody is signed in.

The item is then scoped to a provisioning manifest, and machines leave that manifest once
inventory shows them registered.

## On-demand versus optional installs

They are not related, and the names invite confusion.

| | On-demand item | Optional install |
|---|---|---|
| Manifest section | `managed_installs` (or any section that installs) | `optional_installs` |
| Who decides | Nobody — it runs every session | The user, in Managed Software Center |
| Shown in Managed Software Center | No, it is not offered | Yes, in the software list |
| Runs how often | Every session, indefinitely | Once, when requested |
| Tracked as installed | Never | Yes, normally |
| Detection checks | Skipped entirely | Fully evaluated |

An optional install is a *choice* offered to a user. An on-demand item is a *repetition*
imposed on the machine. If what you want is "make this available", use
[optional installs](Optional-Installs-And-Self-Service).

## On-demand versus recurring

`recurring: true` is the key you probably want for maintenance work. It covers idempotent
tasks that legitimately run every session — a cache clear, a time sync, an account check —
that LoopGuard would otherwise count as a loop.

`recurring` exempts an item from loop suppression and nothing else. Detection still runs, a
receipt is still written, and the item is tracked and reported normally. `OnDemand` adds the
never-installed, no-receipt semantics on top and skips detection.

Choose `recurring` when the item has a real installed state and a real detection check that
happens to keep returning "run me". Choose `OnDemand` when there is no installed state to
speak of.

## Limitations

- The key is `OnDemand`, case-sensitive. `on_demand` is silently ignored.
- Detection metadata on an on-demand item is never evaluated.
- No receipt means no local record and no fleet-wide "is it installed" answer.
- Loop protection is fully disabled for the item — a permanently failing script is never
  suppressed and never flagged as looping.
- Removing the item from a manifest is the only thing that stops it.

## See also

- [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Scripts In pkgsinfo](Scripts-In-pkgsinfo)
- [Install Loop Prevention](Install-Loop-Prevention)
- [Optional Installs And Self Service](Optional-Installs-And-Self-Service)
- [Conditional Items](Conditional-Items)
- [Manifests](Manifests)
- [Item Status Reference](Item-Status-Reference)
