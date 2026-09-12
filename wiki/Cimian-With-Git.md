# Cimian With Git

A Cimian repo is a directory of files, so it can live in version control. Doing that gives you
review before a change reaches the fleet, a history of who promoted what and when, and a way to
revert a bad edit. This page covers what to track and what to leave out, how a review workflow
looks, and the shape of an automated job that rebuilds catalogs and publishes them.

Git is not the delivery mechanism. Clients fetch over HTTP or HTTPS from a web server; the git
repository is the authoring and review side, and something has to get its contents onto that
server.

## What to track

Track the files a human writes:

- `pkgsinfo\` — small YAML, changes constantly, and every change is worth reviewing. This is
  the reason to use git at all.
- `manifests\` — small YAML, and a change here alters what a named machine or group gets.
  Reviewing manifest diffs catches more outages than reviewing pkgsinfo diffs.
- `icons\` — small PNGs that change rarely. Tracking them is cheap and keeps the repo
  self-contained.

## What to leave out, and what it costs you

### Payloads

`pkgs\` holds installer binaries — routinely hundreds of megabytes each, and a repo of any age
holds many versions. Git stores every version of every file forever, so tracking payloads makes
the clone grow without bound and makes it slow for everyone who never needed the old versions.

The usual answer is to ignore `pkgs\` and treat payloads as artifacts published separately —
uploaded to the web server when a package is imported, and pruned with
[repoclean](repoclean) when a version is retired.

The cost is real and worth stating plainly:

- A fresh clone is not a working repo. It has metadata describing payloads it does not have.
- `makecatalogs` warns for every item whose payload it cannot find, and the `--hash_check` size
  comparison cannot run at all. In an automated job you end up passing `--skip_payload_check`,
  which silences the one check that catches a pkgsinfo pointing at a payload that was never
  uploaded.
- Nothing then verifies that the payload a catalog references actually exists until a client
  tries to download it and fails.

If your payloads are small and few, tracking them — with git-lfs or without — buys back the
existence check and the size check. It does not buy back a hash check: `--hash_check` computes
MD5 while `installer.hash` holds a SHA-256 digest, so it reports a mismatch for every item
regardless.

### Catalogs

`catalogs\` is generated. Every file in it is derived entirely from `pkgsinfo\`, and
regenerating is deterministic.

Ignoring it is the conventional choice, and it means whatever publishes the repo must run
`makecatalogs` first. That is a small price and it removes an entire class of merge conflict:
catalogs contain full copies of every item body, so two people promoting different packages on
different branches conflict in a file neither of them edited.

Tracking catalogs buys one thing: a literal record of what was published at each commit, and a
revert that restores it exactly without needing the toolchain. If you do track them, generate
them in one place only — never let two people commit catalogs built on their own machines — and
expect the diffs to be large and unreviewable.

Do not do both halves badly: a repo that tracks catalogs but also rebuilds them in a job will
produce a dirty working tree on every run.

### A starting point

```gitignore
pkgs/
catalogs/
*.msi
*.exe
*.nupkg
*.msix
*.appx
```

The extension rules are belt and braces for a payload that gets dropped somewhere other than
`pkgs\`. Remove them if you deliberately track any installer.

pkgsinfo files can contain PowerShell in literal block scalars, where line endings are part of
the value. Normalising them keeps diffs readable and avoids a whole file appearing changed
because someone's editor rewrote the endings:

```gitattributes
*.yaml text eol=lf
```

## Review workflow

Branch per change, one logical change per branch, and review the diff before merging to the
branch your publish job builds from.

For a **pkgsinfo** diff, a reviewer is checking:

- `version` and `installer.location` change together. A new version pointing at the old
  payload path is the classic mistake, and nothing downstream catches it.
- `installer.hash` and `installer.size` changed if the payload did. A stale hash makes every
  client fail the download after transferring the whole file.
- The `installs` array survived the edit. Losing it means the item can report itself installed
  without anything on disk being checked.
- The `catalogs` list is what was intended, and the names are spelled correctly. A misspelled
  catalog name is not an error — it creates a new catalog nobody reads.
- Inline scripts. `preinstall_script`, `postinstall_script` and friends run as SYSTEM on every
  targeted machine; they deserve the same scrutiny as any other privileged code.

For a **manifest** diff, the reviewer is checking blast radius: which machines this manifest
serves, and whether an `included_manifests` change widens it.

Do not review generated catalogs. If they are tracked, exclude them from review and trust the
pkgsinfo diff — a catalog diff is a mechanical restatement of it.

One behaviour to know about: when the repo path is inside a git checkout, `cimiimport` runs
`git pull` before importing, so an import starts from current metadata. It runs with terminal
prompting disabled and a two-minute cap, so it fails visibly rather than hanging on a credential
prompt — but a failed pull does not stop the import, so on a shared repo, pull deliberately
before you import.

## Rebuilding and publishing from a job

Keep this vendor-neutral: any CI system that can check out a repository, run a Windows command
and copy files can do it. The shape is the same everywhere.

**Trigger** on merge to the branch that represents published state.

**Runner**: Windows, with the Cimian tools available. `makecatalogs` is a self-contained
executable and needs no .NET runtime installed; either install the tools on the runner image or
unpack the published binaries as a step.

**Steps**:

1. Check out the repository.
2. Restore or stage anything that is not tracked. If payloads are ignored, this is where you
   either fetch them or accept that the payload check cannot run.
3. Run `makecatalogs --repo_path <checkout>`, adding `--skip_payload_check` only if the
   payloads are genuinely not present.
4. **Gate on the exit code.** A non-zero exit means at least one pkgsinfo failed to parse.
   Catalogs are written before that check runs, so the working tree at that point holds
   *partial* catalogs — the exit code is a guard, not a rollback. Publishing them would take
   items offline. Fail the job and publish nothing.
5. Publish `catalogs\` and `manifests\` to the web server.
6. Publish `icons\` if it changed.

**The publish step must mirror deletions, not just uploads.** `makecatalogs` deletes any
catalog file that the current pkgsinfo set no longer produces, and manifests get retired too. A
job that only uploads leaves a deleted catalog live on the server indefinitely, still being
served to any client whose manifest still names it. Use a synchronising copy that removes
destination files with no source, and scope it to the directories you actually publish so it
cannot delete `pkgs\`.

**Payloads are a separate path.** They are large, immutable once published, and they must be in
place *before* the catalog that references them goes live. Publishing a catalog first gives
every client an item it cannot download. If payload upload happens in the same job, order it
ahead of the catalog publish.

**Credentials.** `makecatalogs` needs no secrets — it is a local file operation. The only
credentials the job needs are whatever the publish step uses to write to the web server, and
those should be scoped to write access on the repo path and nothing else.

**Verify after publishing** by fetching a catalog over the same URL a client would use, and
confirming the item you expect is in it. A publish that reports success but wrote to the wrong
path is otherwise invisible until a client run fails.

## See also

- [The Cimian Repository](The-Cimian-Repository)
- [Using Catalogs](Using-Catalogs)
- [Promoting Between Catalogs](Promoting-Between-Catalogs)
- [Securing The Repository](Securing-The-Repository)
- [Introduction To pkgsinfo Files](Introduction-To-pkgsinfo-Files)
- [Manifests](Manifests)
- [makecatalogs](makecatalogs)
- [cimiimport](cimiimport)
- [repoclean](repoclean)
