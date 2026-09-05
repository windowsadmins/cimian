# Client Identifier Resolution

Every run of `managedsoftwareupdate` begins by deciding which manifest on the server
belongs to this device. This page covers the candidate chain the client walks, the rule
that decides when it moves on, how to set the identifier, and how to confirm afterwards
which manifest was actually used.

The manifest chosen here is the *primary* manifest. Everything it pulls in through
`included_manifests` is processed as part of it — see [Manifests](Manifests).

## The resolution chain

The client builds an ordered list of candidate manifest names and requests
`<SoftwareRepoURL>/manifests/<candidate>.yaml` for each in turn. The first one the server
returns is the primary manifest, and resolution stops there.

| # | Candidate | Kind |
|---|---|---|
| 1 | Client certificate common name | configured |
| 2 | `ClientIdentifier` from the client configuration | configured |
| 3 | The machine's hostname | probe |
| 4 | The BIOS serial number | probe |
| 5 | `Orphaned` | catch-all |
| 6 | `site_default` | catch-all |

Blank candidates are skipped, and candidates that resolve to the same string are tried
only once — if `ClientIdentifier` is set to the hostname, that name is requested once,
not twice.

Candidates are resolved lazily. The BIOS serial number comes from a hardware query that
is only issued if the chain actually reaches step 4, so a device with a working
configured identifier never pays for it.

The certificate common name is only ever a candidate when the client is configured to use
a client certificate *and* to treat its common name as the identifier. Otherwise step 1
contributes nothing and the chain starts at `ClientIdentifier`.

## Only a 404 advances the chain

This is the behaviour to understand before anything else.

**A candidate advances to the next one only when the server answers HTTP 404.** Any other
failure — an authentication rejection, a 5xx, a TLS failure, a network error — aborts
resolution immediately. The client logs that it is aborting at that candidate and
explicitly refuses to fall through to a catch-all, and the device gets no managed items
for that run.

That is deliberate. Without it, a repository outage or an expired credential would look
identical to "this device has no manifest", and every affected device would quietly demote
itself onto `site_default` and start installing — or, worse, removing — whatever the
catch-all says. A transient server error must never silently rewrite a fleet's policy. A
run that fails loudly and changes nothing is the correct outcome.

The practical consequence: if devices suddenly appear on a catch-all manifest, the cause is
genuinely 404s, not a server problem. A server problem produces failed runs, not demoted
ones.

## Logging along the chain

The chain reports at a severity that matches how deliberate each candidate is:

- A 404 on a **configured** candidate (certificate CN or `ClientIdentifier`) is a warning:
  someone set that identifier and no manifest matches it.
- A 404 on a **probe** or **catch-all** candidate is routine detail — most sites do not
  name manifests after serial numbers, so those 404s are expected.
- Resolving on anything other than the first candidate tried logs a warning naming the
  manifest and its kind, and states that the device is running on a fallback
  configuration.
- Exhausting every candidate logs a warning listing all names tried and states that the
  device will have no managed items this run.

## Setting the identifier

The identifier lives in the client configuration file at
`%ProgramData%\ManagedInstalls\Config.yaml`:

```yaml
SoftwareRepoURL: https://cimian.example.com/repo
ClientIdentifier: WS-0001
```

To drive the identifier from a client certificate instead, both switches must be on:

```yaml
UseClientCertificate: true
UseClientCertificateCNAsClientIdentifier: true
```

With those set, the certificate's common name is URL-escaped and used as the manifest
name, taking precedence over `ClientIdentifier`. If the certificate cannot be read, or
either switch is off, this candidate is skipped and `ClientIdentifier` is used as normal.

Leaving `ClientIdentifier` unset is a legitimate choice: the hostname probe at step 3 then
does the work, which is the right model when manifests are named after machines. Setting
it explicitly is the right model when a device's role is not derivable from its name, or
when you want the identifier to survive a rename.

The configuration file can be delivered by whatever manages your endpoints — see
[Configuring-Clients-With-Intune](Configuring-Clients-With-Intune) and
[Client-Configuration](Client-Configuration).

## Seeing which manifest a client used

To check what the client believes its identifier is, without contacting the server:

```powershell
managedsoftwareupdate --show-config
```

That prints `ClientIdentifier` alongside the repository URL and the local manifest cache
path.

To see the resolution actually happen, and what it produced, run a verbose check-only
session. It changes nothing on the device:

```powershell
sudo managedsoftwareupdate -v --checkonly
```

The `MANIFEST RETRIEVAL` section shows each candidate tried and why the chain moved on,
followed by a `MANIFEST HIERARCHY` tree that shows the primary manifest, every manifest it
included, and which items came from each. That tree is the authoritative answer to "why is
this device installing that" — every item is attributed to the manifest that listed it.

Manifests fetched during the run are also written to the local cache, so the files present
under `%ProgramData%\ManagedInstalls\manifests\` are the ones this device actually
retrieved.

For testing a manifest before publishing it, the client can be pointed at a file on disk,
bypassing the whole chain:

```powershell
sudo managedsoftwareupdate -v --checkonly --local-only-manifest C:\temp\candidate.yaml
```

## The `Orphaned` manifest as an operational tool

`Orphaned` is a catch-all, but it is a more useful one than `site_default` because of where
it sits: a device reaches it only after its configured identifier, its hostname and its
serial number have all 404'd. That is a precise signal — the device is managed by Cimian,
is reaching the repository successfully, and no manifest in the repository claims it.

Publish an `Orphaned.yaml` and you convert that silence into something visible and
actionable. Useful contents:

- An inventory or reporting agent, so the device keeps checking in and appears in your
  reporting while unclaimed.
- Nothing that installs software a user would notice, and in particular nothing in
  `managed_uninstalls` — a device lands here by accident, and an accident should not remove
  anything.
- A catalog list, so the manifest is functional rather than inert.

```yaml
name: Orphaned
catalogs:
  - Production
managed_installs:
  - ExampleInventoryAgent
```

Because a fallback resolution logs a warning naming the manifest, devices sitting on
`Orphaned` are straightforward to find in your logging or reporting: they are the ones
whose runs report a catch-all primary manifest. Treat that list as a work queue — each
entry is a device that needs a manifest, or a device whose identifier is wrong.

If you would rather an unclaimed device get nothing at all, publish neither `Orphaned` nor
`site_default`. The chain then exhausts, the client warns that no manifest could be
resolved, and the device makes no changes.

## See also

- [Manifests](Manifests)
- [Client-Configuration](Client-Configuration)
- [Configuring-Clients-With-Intune](Configuring-Clients-With-Intune)
- [managedsoftwareupdate](managedsoftwareupdate)
- [Securing-The-Repository](Securing-The-Repository)
- [Troubleshooting](Troubleshooting)
- [Logging](Logging)
