# The Download Cache

Cimian downloads every installer payload to a local cache before running it. The cache makes
a retried install free and lets a large download resume across runs, but it is also the
single largest thing Cimian writes to a client disk, and it only stays bounded if retention
is configured. This page covers the layout, how a cached payload is validated and reused,
resume behaviour, and the retention and cleanup rules an operator needs.

## Where the cache lives

The default is `%ProgramData%\ManagedInstalls\Cache`, overridable with `CachePath` in
`Config.yaml`.

Inside it, a payload goes into a subdirectory named after the item's `category`, lowercased
with spaces replaced by underscores. An item with no category goes into the cache root:

```
C:\ProgramData\ManagedInstalls\Cache\
    design_tools\
        ExampleApp-4.2.0.msi
        ExampleApp-4.2.0.msi.verified
    utilities\
        ExampleUtility-1.9.exe
    ExampleUncategorised-2.0.msi
```

Two sidecar file types sit alongside payloads:

| Suffix | Meaning |
|---|---|
| `.downloading` | An in-flight or interrupted partial download |
| `.verified` | A marker recording that this exact file already passed its hash check |

### Never set `CachePath` to an empty string

`CachePath: ""` is not the same as omitting the key. Cimian normalises a blank or
whitespace-only value back to the default, because without that normalisation every cache
path resolved relative to the process working directory — which for the watcher service is
the Cimian install directory. Payloads landed under `%ProgramFiles%\Cimian`, outside every
cleanup rule, and grew without limit. If you are not relocating the cache, leave the key out.

## How a cached payload is reused

Reuse is driven entirely by `installer.hash`, the payload's SHA-256 digest. Nothing else
validates a cached file.

When a payload is needed and a file already exists at its cache path:

1. If a `.verified` marker exists and still matches the file's size and last-write time, the
   file is reused immediately. Nothing is re-hashed. This exists because hashing a multi-gigabyte
   package costs minutes of a run that has a limited time to live.
2. Otherwise the file is hashed. A match logs `Using cached file: <name>`, writes a fresh
   `.verified` marker, and reuses it.
3. A mismatch clears the marker and re-downloads.

**A pkgsinfo with no `installer.hash` gets no cache reuse at all.** The existence check is
gated on having an expected hash, so an unhashed payload is downloaded again in full on every
run that needs it. On a large installer that is the whole run's budget, every hour. Hash your
payloads — `cimipkg` and `cimiimport` write the digest for you.

The same hash is checked after a fresh download. A mismatch deletes the temporary file and
raises an error, which the retry loop treats like any other failure and retries.

## Downloading and resume

A partial download is written to `<payload>.downloading` and only moved into place once it is
complete and verified.

Before downloading, Cimian issues a `HEAD` request (30-second timeout) to learn the payload
size and whether the server advertises `Accept-Ranges: bytes`. If a partial exists and the
server supports ranges, the download resumes with a `Range: bytes=<offset>-` request. A `416`
response means the partial no longer lines up with the payload, so it is deleted and the
download restarts from zero.

| Behaviour | Value |
|---|---|
| Base download timeout | 10 minutes |
| Timeout for larger payloads | 2 minutes plus 1 minute per 50 MB, when that exceeds 10 minutes |
| Retries | 5, with exponential backoff |
| Stall check interval | 30 seconds |
| Stall threshold | Under 50 KB/s for two consecutive checks (120 seconds) |

A stalled download raises a distinct error and **preserves** the partial so the next attempt
resumes rather than starting over. Any other failure that exhausts the retries deletes the
partial. A completed transfer whose byte count does not match the advertised `Content-Length`
is treated as an incomplete download and retried.

A relative `installer.location` is fetched from `<SoftwareRepoURL>/pkgs<location>`. An
absolute `http://` or `https://` location is used as given.

### Precaching

An `optional_installs` item whose pkgsinfo sets `precache: true` has its payload downloaded
during the run even though nobody has asked for it, so that a later user-initiated install
from Managed Software Center is immediate. Script-only items with no installer location are
skipped. Precached payloads live in the cache like any other and are subject to the same
retention rules — a precached item that nobody installs is eventually pruned and precached
again.

## Retention and cleanup

Every normal session runs two cleanup passes over the cache, in order, before the download
phase. Running them before downloading is deliberate: the worst case for an over-eager delete
is one re-download.

### Pass 1 — validation

Removes files that cannot be useful:

- zero-byte files
- `.verified` markers whose payload no longer exists
- `.downloading` partials older than **24 hours**

This pass is not configurable.

### Pass 2 — retention

Deletes any cache file whose last-write time is older than `CacheRetentionDays`, then removes
the category directories left empty behind it. `.downloading` and `.verified` files are
skipped — partials have their own 24-hour rule, and a marker is reaped by the orphan rule once
its payload is gone. When a payload is deleted its paired marker goes with it.

```
Cache retention: removed 14 entries older than 30 days, reclaimed 41,208 MB
```

| `CacheRetentionDays` | Effect |
|---|---|
| `30` (default) | Payloads untouched for 30 days are deleted each run |
| any positive integer | Same, with that window |
| `0` or negative | **Retention is disabled. The cache is never pruned.** |

`CacheRetentionDays` can also be set by policy at
`HKLM\SOFTWARE\Policies\Cimian`, as a `REG_DWORD` or a `REG_SZ`, and the policy value wins
over `Config.yaml`. See [Client Configuration](Client-Configuration).

### How the cache grows without bound

Every version of every package a machine has ever been offered leaves a payload behind.
Superseded versions are never referenced again, so nothing renews their last-write time and
nothing else deletes them. With retention disabled — `CacheRetentionDays: 0`, or a value the
policy has zeroed — a machine that tracks a handful of large, frequently-rebuilt suites
accumulates tens of gigabytes and keeps going. The symptom operators usually see first is not
a disk-space alert but a run failing to download because the volume is full, which then
reports as an install failure on whatever package was unlucky.

Two other configurations produce the same result with retention nominally enabled: a blank
`CachePath` (see above), which puts payloads outside the swept tree entirely, and a cache
relocated to a path that some other tool is also writing to.

## Measuring the cache

The client reports its own view:

```
managedsoftwareupdate --cache-status
```

That prints the configured cache path, the file count, the total size in GB, the age of the
oldest file, and a count of zero-byte files it considers corrupt.

To see where the space has actually gone, measure per category:

```powershell
Get-ChildItem 'C:\ProgramData\ManagedInstalls\Cache' -Directory |
    Select-Object Name, @{n='GB';e={
        '{0:N2}' -f ((Get-ChildItem $_.FullName -Recurse -File |
            Measure-Object Length -Sum).Sum / 1GB)
    }} | Sort-Object GB -Descending
```

To find the individual payloads worth deleting, list the largest files with their ages:

```powershell
Get-ChildItem 'C:\ProgramData\ManagedInstalls\Cache' -Recurse -File |
    Where-Object { $_.Extension -notin '.verified', '.downloading' } |
    Sort-Object Length -Descending | Select-Object -First 20 Name, LastWriteTime,
        @{n='GB';e={'{0:N2}' -f ($_.Length / 1GB)}}
```

Multiple versions of one package sitting side by side is the signature of retention being off.

## Cleaning the cache

**Preferred: fix the retention setting and let the next run sweep.** Set
`CacheRetentionDays` to a value shorter than your problem — 7 or 14 while you recover, 30
afterwards — and run:

```
managedsoftwareupdate --checkonly
```

The retention pass runs in check-only mode, so this reclaims space without installing
anything.

To run both cleanup passes on their own, without starting a session:

```
managedsoftwareupdate --validate-cache
```

That runs the validation pass and then the retention pass, so it honours
`CacheRetentionDays` exactly as a normal run does. It is the quickest way to apply a
retention change you have just made.

### `--clean-cache` deletes everything

```
managedsoftwareupdate --clean-cache
```

This is a different and far more aggressive operation than the retention sweep. It deletes
**every file** under the cache directory regardless of age, regardless of
`CacheRetentionDays`, and regardless of whether the payload is about to be needed. It then
removes the emptied subdirectories and exits without running a session. Partial downloads go
with everything else, so an interrupted multi-gigabyte transfer restarts from zero.

Two things to know before using it:

- It targets the **default** cache location, `%ProgramData%\ManagedInstalls\Cache`, not
  `CachePath`. On a client with a relocated cache it cleans the wrong directory and reports
  success. Older clients targeted a path that has never existed and cleaned nothing at all.
- It is safe in the sense that nothing is lost permanently — every payload can be fetched
  again — but the next run re-downloads whatever it still needs, which on a large fleet is a
  deliberate decision about your repository's bandwidth, not a routine one.

Use it when you know the cache contents are wrong, not as routine housekeeping.

## Keys that do not do what their name suggests

- **`UseCache`** is a recognised `Config.yaml` key, defaults to `true`, and is printed by
  `managedsoftwareupdate --show-config`. **Nothing reads it.** Setting it to `false` does not
  disable caching. Caching is unconditional; the only lever over cached payloads is
  `CacheRetentionDays`.
- `shared` configuration models carrying keys such as `max_concurrent_downloads`,
  `download_timeout` and `verify_hashes` exist in the codebase but are not the client's
  configuration and are not read by the download path. Do not put them in `Config.yaml`.
- Downloads are sequential. There is no concurrency setting.

## See also

- [Client Configuration](Client-Configuration)
- [Introduction To pkgsinfo Files](Introduction-To-pkgsinfo-Files)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [The Cimian Repository](The-Cimian-Repository)
- [managedsoftwareupdate](managedsoftwareupdate)
- [Install Loop Prevention](Install-Loop-Prevention)
- [Troubleshooting](Troubleshooting)
