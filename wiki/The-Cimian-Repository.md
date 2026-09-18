# The Cimian Repository

A Cimian repo is a directory of YAML metadata and installer payloads, published over HTTP
or HTTPS. There is no Cimian server component: clients make plain GET requests for files at
fixed paths, so any static web server will do. This page covers the on-disk layout, the URLs
a client constructs against it, what the serving side has to get right, and how to create an
empty repo from nothing.

If you have run a Munki repo, the shape will be familiar. The directory names and the URL
paths are the same idea, but the file formats are YAML and the key sets differ.

## Layout

```
C:\CimianRepo\
  catalogs\
  icons\
  manifests\
  pkgs\
  pkgsinfo\
```

Those five directories are the whole repo. Cimian reads nothing else, and no other directory
name is significant.

`pkgsinfo\` holds package metadata, one `.yaml` file per package version. `makecatalogs`
scans it recursively, so you can nest subdirectories by vendor, category or team as you
like — the layout carries no meaning. Only the `.yaml` extension is scanned; a file named
`ExampleApp.yml` is invisible and its package silently does not exist. Clients never fetch
anything from `pkgsinfo\`; it is authoring input only.

`pkgs\` holds the installer payloads. The `installer.location` key in a pkgsinfo is a path
relative to this directory, so `location: apps/ExampleApp-1.2.0.msi` means
`C:\CimianRepo\pkgs\apps\ExampleApp-1.2.0.msi`. Backslashes in a location are converted to
forward slashes before the URL is built, so either separator works in the YAML. A location
that already begins with `http://` or `https://` is used verbatim and `pkgs\` is bypassed
entirely, which lets a catalog point at a payload hosted somewhere else.

`catalogs\` holds generated files — one per catalog name, written by `makecatalogs`. Never
edit them by hand and never treat them as source. `makecatalogs` creates the directory if it
is missing, overwrites every catalog it builds, and deletes any `catalogs\*.yaml` whose name
is no longer produced by the current set of pkgsinfo files.

`manifests\` holds the per-client lists of what should be installed, one `.yaml` file per
manifest name. Subdirectories are supported: an `included_manifests` entry may contain a
path, and the client fetches it under `manifests/`.

`icons\` is flat — no subdirectories. A filename containing `/`, `\` or `..` is rejected by
the client and never requested. An item's icon is `icon_name` when set, otherwise
`<name>.png`.

## How the client builds URLs

The single client setting that points at a repo is `SoftwareRepoURL` in
`C:\ProgramData\ManagedInstalls\Config.yaml`. Every request is that value with any trailing
slash trimmed, plus a literal path segment:

| Content | URL |
|---|---|
| Manifest | `{SoftwareRepoURL}/manifests/{name}.yaml` |
| Catalog | `{SoftwareRepoURL}/catalogs/{name}.yaml` |
| Icon | `{SoftwareRepoURL}/icons/{filename}` |
| Package payload | `{SoftwareRepoURL}/pkgs/{installer.location}` |

With `SoftwareRepoURL: https://cimian.example.com/repo`, a manifest named `site_default`
is fetched from `https://cimian.example.com/repo/manifests/site_default.yaml` and the payload
above from `https://cimian.example.com/repo/pkgs/apps/ExampleApp-1.2.0.msi`.

Only the icon filename is URL-encoded. Manifest and catalog names are interpolated into the
URL raw, so a name containing a space or another character that needs escaping does not work.
Keep manifest and catalog names to plain identifiers.

Names are also matched literally. `makecatalogs` buckets catalog names case-insensitively but
writes the file under the first spelling it encounters, and the client requests exactly the
spelling in the manifest. On a case-sensitive web server, `Production` and `production` are
two different URLs and one of them 404s.

## Serving the repo

**Only HTTP and HTTPS are supported.** The client validates `SoftwareRepoURL` and rejects any
other scheme, so `file://` paths, UNC shares and object-storage-native protocols such as `s3://`
or `abfss://` cannot be used. Object storage works only when it is fronted by something that
speaks plain HTTP GET over the same path layout.

Any static web server is sufficient. Cimian needs no server-side application, no API and no
database. What it does need:

**Direct GETs at exact paths.** The client never lists a directory, never asks for an index and
never walks the tree. It requests only the four path shapes above. Directory listing can and
should be disabled.

**Correct 404s.** The status code is load-bearing in one place: when resolving which manifest
belongs to a device, only an HTTP 404 advances the client to the next candidate name. Any other
failure — 401, 403, 500, a TLS error, a connection reset — aborts manifest resolution outright
rather than falling through to a catch-all manifest, and the device gets no managed items that
run. A server or proxy that answers missing files with a login page, a `200` "not found" body or
a `403` breaks the fallback chain.

**A MIME mapping for every extension you serve.** Cimian never inspects `Content-Type`; it reads
the body. But some servers refuse to serve a file whose extension has no MIME mapping and answer
404 — IIS behaves this way by default — and the client cannot tell that apart from a genuinely
missing file. Map at least `.yaml`, plus every payload extension you ship: `.msi`, `.exe`,
`.nupkg`, `.msix`, `.appx`, `.ps1`, and `.png` for icons.

**`Last-Modified` on icons.** Icon sync sends `If-Modified-Since` and treats `304` as "unchanged".
A server that does not answer conditional requests makes every client re-download every icon on
every run.

**`HEAD` and byte ranges on payloads.** Before downloading a payload the client issues a `HEAD` to
read `Content-Length` and check for `Accept-Ranges: bytes`. A failed `HEAD` is not fatal, but it
costs you two things: interrupted downloads restart from zero instead of resuming, and the
per-download timeout stays at the 10-minute default instead of being scaled up from the file size.
For multi-gigabyte payloads that is the difference between a download that completes and one that
is cancelled mid-transfer.

**Stable payload URLs.** A payload is verified against the SHA-256 in `installer.hash` after
download and again before a cached copy is reused. Replacing the bytes at an existing path
without changing the pkgsinfo hash makes every client fail that item. Publish a new version at a
new path instead.

Authentication is optional and is a client-side concern: HTTP Basic, a bearer token, or mutual
TLS. Cimian sends no `Authorization` header unless one of those is configured, supports no
custom or extra request headers, no Windows Integrated authentication, and no storage-provider
signed-URL scheme. There is also no proxy support — the client sets no proxy of its own. See
[Securing The Repository](Securing-The-Repository).

## Create an empty repo

Make the four authored directories. `catalogs\` is left out deliberately; `makecatalogs`
creates it.

```powershell
New-Item -ItemType Directory -Path C:\CimianRepo\pkgsinfo, C:\CimianRepo\pkgs, C:\CimianRepo\manifests, C:\CimianRepo\icons
```

Generate the catalogs. With an empty `pkgsinfo\` this produces a single `catalogs\All.yaml`
with no items, which is the correct starting state. `makecatalogs` fails if `pkgsinfo\` does
not exist, so create the directories first.

```powershell
makecatalogs --repo_path C:\CimianRepo
```

Write a catch-all manifest. `site_default` is the last name the client tries, so every device
that matches nothing more specific lands here.

```powershell
@'
name: site_default
catalogs:
  - Production
managed_installs: []
optional_installs: []
'@ | Set-Content -Encoding utf8 C:\CimianRepo\manifests\site_default.yaml
```

Publish `C:\CimianRepo` as the document root of a static site, then point a client at it by
setting `SoftwareRepoURL` in its `Config.yaml`. Confirm the plumbing from the client before
importing anything:

```powershell
managedsoftwareupdate --checkonly -vv
```

A run that resolves a manifest and loads a catalog with zero items has proved the whole path.
See [Getting Started](Getting-Started) and [Client Configuration](Client-Configuration).

## Configuring an admin workstation

The tools that write into the repo find it through `C:\ProgramData\ManagedInstalls\Config.yaml`
on the machine you author from, unless you pass a path on the command line.

They do not agree on the key name. `cimiimport` and `makepkginfo` read `RepoPath`;
`makecatalogs` and `manifestutil` read `repo_path`. Both spellings are ignored silently by the
tool that does not want them, so a config with only one of them makes half the toolchain report
that the repo is not configured. Set both to the same value:

```yaml
RepoPath: C:\CimianRepo
repo_path: C:\CimianRepo
DefaultCatalog: Development
DefaultArch: x64,arm64
```

`repoclean` takes its path only from `--repo-url` and reads no config file.

Note that this is the *admin* `Config.yaml`, which shares a filename with the client
configuration. On a machine that is both an admin workstation and a managed client, the two
sets of keys live in the same file; they do not collide.

## What writes into the repo

- [cimiimport](cimiimport) — the usual path. Imports an installer: extracts metadata, copies
  the payload into `pkgs\`, writes the pkgsinfo into `pkgsinfo\`, then runs `makecatalogs`.
- [makepkginfo](makepkginfo) — writes pkgsinfo YAML to stdout without touching the repo, or
  creates a stub in `pkgsinfo\` with `--new`.
- [makecatalogs](makecatalogs) — the only writer of `catalogs\`.
- [manifestutil](manifestutil) — creates manifests and adds or removes items in them.
- [repoclean](repoclean) — removes superseded versions from `pkgsinfo\` and `pkgs\`. It is a
  dry run unless you pass `--remove`.
- Icons are copied into `icons\` by hand, or extracted by `cimiimport --extract-icon`.

## See also

- [Using Catalogs](Using-Catalogs)
- [Promoting Between Catalogs](Promoting-Between-Catalogs)
- [Cimian With Git](Cimian-With-Git)
- [Securing The Repository](Securing-The-Repository)
- [Manifests](Manifests)
- [Introduction To pkgsinfo Files](Introduction-To-pkgsinfo-Files)
- [Client Configuration](Client-Configuration)
- [The Download Cache](The-Download-Cache)
