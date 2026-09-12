# Managed Profiles And Managed Apps

`managed_profiles` and `managed_apps` are manifest keys that Cimian parses but does not act
on. They were added as a placeholder for delivering configuration profiles and Store
applications through an external MDM pipeline, and the client side of that pipeline does not
exist. This page says exactly what the keys do today, so nobody plans around behaviour that
is not there.

## Verdict

**Not implemented.** Cimian reads these two manifest keys, turns each entry into an item, and
then deliberately skips it. Nothing is installed, removed, configured or reported. There is
no Microsoft Graph integration, no Intune integration, and no external pipeline shipped with
Cimian that consumes these entries.

Do not use these keys to deploy anything. Deploy configuration profiles and Store
applications with your MDM directly.

## What the client actually does

The keys are accepted at manifest level and inside `conditional_items`:

```yaml
name: WORKSTATION-01
catalogs:
  - Production
managed_installs:
  - Example App
managed_profiles:
  - ExampleSecurityProfile
managed_apps:
  - Example Store App
```

For each name listed, the client creates a manifest item with the action `profile` or `app`,
and then, at the point where it decides what to do with each item:

```
    Skipping external item: ExampleSecurityProfile (action: profile)
```

That is the whole behaviour. Specifically, for a `profile` or `app` item Cimian does **not**:

- look the name up in any catalog
- run a status check of any kind
- download anything
- install, remove or configure anything
- write a `ManagedInstalls` receipt
- emit an install event, a `profile` event or an `app` event
- include it in `items.json`
- show it in Managed Software Center

A name listed under `managed_profiles` or `managed_apps` does not need to exist in a catalog.
Nothing looks for it.

## The side effects that do exist

The items are inert, but they are still items, so they participate in three pieces of
manifest logic. These are the only observable consequences of using the keys.

**They are counted separately in the run summary.** The client reports how many manifest
items it is actually managing and how many it is excluding:

```
Managed items: 42 (excludes 3 MDM profiles/apps)
```

Profile and app entries are excluded from the installs, updates and removals counts in the
session summary.

**They lose every deduplication contest.** If the same name appears both as a
`managed_profile` and under any other manifest section, the other action wins — `profile` and
`app` sit at the bottom of the action precedence ladder, below `optional`. So listing a real
package name under `managed_apps` as well as `managed_installs` changes nothing; the install
proceeds.

**They block two things, though.** In the two places where the engine checks manifest intent
rather than action rank, a `profile` or `app` claim on a name is honoured:

- A transitive dependency is skipped if the manifest also lists its name as a profile or app.
  A package that `requires` that name will fail to install its dependency.
- A user's self-service install or removal request in Managed Software Center is refused for
  a name the manifest lists as a profile or app, on the grounds that it is administrator
  intent.

Both of those are traps rather than features. Listing a real package's name under
`managed_apps` will quietly prevent it being installed as a dependency and prevent users
requesting it, while doing nothing else at all.

## The pkgsinfo-level keys

`makepkginfo` can write `managed_profiles` and `managed_apps` into a **pkgsinfo** file. Those
fields go no further: `makecatalogs` does not carry them into a catalog, and the client's
catalog model has no such field. A pkgsinfo carrying them is valid and the keys are dropped
in transit.

## What is documented elsewhere and is not true

An older page in this wiki, `managed-profiles-apps-guide.md`, describes these keys as a
working Microsoft Graph API integration. Its specific claims do not hold at this release:

| Claim | Reality |
|---|---|
| Profiles and apps are deployed via Microsoft Graph | No Graph code exists in Cimian |
| Actions are logged with `event_type: "profile"` / `"app"` | The only event types written are `install` and `status_check` |
| Items appear in reports with `item_type: "managed_profiles"` / `"managed_apps"` | The internal type strings are `managedprofile` and `managedapp`, and rows carrying them are **filtered out** of `items.json` |
| A pipeline reads deployment events from `reports\events.json` | No such events are ever written |
| Session tracking includes counts of profiles and apps scheduled | The summary counts them only in order to exclude them |

The deduplication and conditional-item behaviour that page describes is real. Everything
about deployment and reporting is not.

## If you need this today

Use your MDM for profiles and Store apps, and Cimian for packaged software. If you want the
two visible in one place, report on both from your inventory system rather than trying to
express MDM intent in a Cimian manifest.

For Store applications specifically, Cimian does install MSIX and APPX payloads as ordinary
packages — see [Installer Types](Installer-Types). That is a different mechanism from
`managed_apps` and it works.

## See also

- [Manifests](Manifests)
- [Conditional Items](Conditional-Items)
- [Installer Types](Installer-Types)
- [Deploying Cimian With Intune](Deploying-Cimian-With-Intune)
- [Configuring Clients With Intune](Configuring-Clients-With-Intune)
- [Reporting Data Contract](Reporting-Data-Contract)
