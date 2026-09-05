# Force Installs And Deadlines

`force_install_after_date` puts a deadline on a package. Once the deadline passes, Cimian
stops honouring the two things that would otherwise let the package sit uninstalled: an
install window, and a user's choice not to install an optional item. This page covers the
key's format, exactly what changes when the deadline passes, what the user sees, and — just
as importantly — the parts of a deadline story that Cimian does not implement.

## The key

`force_install_after_date` is a pkgsinfo key holding a single date or date-time. It is
carried through to the catalog and read by the client.

```yaml
name: Example App
version: 4.2.0
force_install_after_date: 2026-10-01 09:00:00
installer:
  location: /apps/ExampleApp-4.2.0.msi
  type: msi
  hash: 4f1c9d0b8a6e2f37c5d41b90ae7362f8d0c1a5b4e93827d6f0a1b2c3d4e5f607
installs:
  - type: msi
    product_code: '{A1B2C3D4-E5F6-4708-9A0B-1C2D3E4F5061}'
    version: 4.2.0
```

A plain date is also valid and means midnight at the start of that day:

```yaml
force_install_after_date: 2026-10-01
```

### Time zone

The deadline is compared against the client's **local system clock**, with no time-zone
conversion. Cimian does not interpret the value as UTC and does not convert an offset or a
`Z` suffix into local time before comparing, so a value written as `2026-10-01T09:00:00Z`
does not fire at 09:00 UTC — it fires when each machine's own wall clock reaches that
reading.

Write the deadline as a plain local date or date-time and accept that a geographically spread
fleet crosses it at different absolute moments. If you need a single global instant, pick the
date-time in the time zone your fleet is in and give yourself margin.

## What changes when the deadline passes

Exactly two things. Both are evaluated on every run.

### It overrides an install window

An item with an [install window](Supported-pkgsinfo-Keys) is normally dropped from the run
when the current time is outside that window, and is reported as pending with reason code
`deferred_install_window`. Once `force_install_after_date` is in the past, the item stays in
the queue and installs regardless of the window, reported with reason code
`deadline_overrides_window`:

```
Installing Example App v4.2.0 despite install_window 02:00-05:00: force_install_after_date 2026-10-01 has passed
```

This applies to installs, updates and removals alike — the window filter covers all three
lists, and the deadline override is checked in all three.

### It forces an optional install

An item listed in `optional_installs` is normally offered in Managed Software Center and
installed only if a user asks for it. Once the deadline is in the past, Cimian status-checks
the item on every run and, if it needs action, queues it as a normal install or update:

```
    -> force_install_after_date 2026-10-01 has passed, forcing install of optional item Example App
```

Reason code `force_install_deadline`. Two eligibility gates still apply before the item is
queued: the item's minimum OS version and its minimum Cimian client version. An ineligible
item is skipped with the corresponding reason, not forced.

This is the main use of the key. It turns "available if you want it" into "available now,
mandatory from the first of the month", without you having to edit manifests on the deadline
day.

### For a plain managed install, the key changes nothing

An item in `managed_installs` with no install window is already mandatory: it is queued on
every run until it is installed. Adding `force_install_after_date` to it has no observable
effect, before or after the date. The key only matters where something else would otherwise
be holding the install back.

## What still stops a past-deadline install

The deadline overrides the install window filter and the optional-item gate. It overrides
nothing else. In particular, an item whose deadline has passed is still deferred by every one
of the following:

| Gate | Behaviour with a passed deadline |
|---|---|
| `blocking_applications` running | Deferred for the whole run, reason code `blocking_apps` |
| Auto mode with an active user, when `unattended_install` is not `true` | Deferred |
| Auto mode with an active user, when `restart_action` would interrupt the user | Deferred, even with `unattended_install: true` |
| An open LoopGuard suppression window | Suppressed |
| Failed dependency, disk space, architecture or OS-version ineligibility | Not installed |

The order matters. The install-window filter runs first, so a deadline item survives it — and
is then handed to the blocking-application filter and the active-user filter, either of which
can drop it again. **A deadline is not an escalation path past a user's running application.**

For a deadline to be enforceable on a machine somebody is using, the package needs
`unattended_install: true` and a `restart_action` that does not interrupt the user. Without
that, the package waits for a session with no active user — which on a desk machine may be
overnight, or may be never.

```yaml
name: Example App
version: 4.2.0
force_install_after_date: 2026-10-01 09:00:00
unattended_install: true
installer:
  location: /apps/ExampleApp-4.2.0.msi
  type: msi
```

Pairing a deadline with `blocking_applications` on the same package guarantees the deadline
cannot be met while the application is open. That is sometimes what you want, but it is worth
choosing deliberately rather than discovering it.

## What the user sees

Managed Software Center reads the deadline from `InstallInfo.yaml` and surfaces it in three
places.

**Per item.** An item with a deadline carries a warning line that sharpens as the date
approaches:

| Time remaining | Text |
|---|---|
| More than a day | `This item must be installed by <date and time>` |
| Under a day | `This item must be installed within N hours` |
| Under an hour | `This item must be installed within the hour!` |
| Past | `This item is past its installation deadline!` |

**A window banner.** The nearest deadline among the session's managed installs and removals
produces a banner: an "must be installed by" line inside three days, and an "Urgent" line
inside one day. Outside three days there is no banner. The banner is computed from the
managed-install and removal lists only, so an *optional* item's deadline does not raise it.

**Ordering.** Items with a deadline sort to the top of both the updates and the installs
lists, nearest deadline first.

Once any item is past its deadline, Managed Software Center enters its insistent mode and
navigates to the updates page. That is a presentation change in the self-service GUI; it does
not, on its own, install anything.

## What Cimian does not do at a deadline

State these plainly to whoever is setting the policy, because several of them are things
Munki admins reasonably expect:

- **Nothing is forced logged out or restarted because a deadline passed.** Restart and logout
  come only from the package's own `restart_action`, only after a successful install, and
  only in `auto` or `bootstrap` mode. A missed deadline produces no shutdown, no countdown
  window, and no forced sign-out.
- **There is no deadline countdown dialog and no deferral counter.** The client does not
  present the user with a "you may postpone this N more times" prompt. What the user gets is
  the Managed Software Center text described above, and only if they open it.
- **The deadline does not imply unattended installation.** It does not set
  `unattended_install`, and it does not bypass the active-user check.
- **The deadline does not clear a LoopGuard suppression.** A package that is suppressed for
  looping stays suppressed past its deadline. See
  [Install Loop Prevention](Install-Loop-Prevention).
- **The deadline is not enforced by the GUI.** Managed Software Center displays it; the
  installs happen in the normal `managedsoftwareupdate` session.
- **There is no separate "force install" or "deadline" state in reporting.** A forced item is
  reported as a normal pending install or update, distinguished only by its reason code.

## Choosing a deadline

Give the fleet enough runs to hit the deadline. The scheduled task runs hourly, but every
one of the deferral gates above can consume runs, so a deadline set for tomorrow on a package
with blocking applications is a deadline for the machines that happen to be idle.

A workable pattern for a mandatory upgrade is: publish as an optional install with the
deadline set several weeks out, let people take it on their own schedule, and let the
deadline pick up the remainder. Mark it `unattended_install: true` so the remainder can
actually be picked up.

Removing the key later is safe — the item reverts to being optional or window-bound on the
next catalog refresh — but a package already installed by the deadline stays installed.

## See also

- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Optional Installs And Self Service](Optional-Installs-And-Self-Service)
- [Blocking Applications](Blocking-Applications)
- [Managed Software Center](Managed-Software-Center)
- [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Install Loop Prevention](Install-Loop-Prevention)
- [Item Status Reference](Item-Status-Reference)
