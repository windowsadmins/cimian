# Version Comparisons

Almost every "is this item current" answer ends in a version comparison: catalog version
against installed version, and catalog version against catalog version when the same item
appears in more than one catalog. This page describes exactly how the client orders two
version strings, including the rule that surprises people most — an unparseable version
compares **equal**, so an item carrying one never updates.

## Where comparisons happen

The same ordering is used for:

- the version reported by a `version_script` against the catalog version
- a file's version resource against the expected version in an `installs` entry
- an MSI's registered `DisplayVersion` against an `installs` entry's version
- a `check.registry.version` or `check.file.version` sub-check
- the managed-install receipt against the catalog version
- picking a winner when two catalogs both carry an item — **highest version wins, not
  catalog order**
- deciding whether the running client is already at or above the catalog's client version

The result is a three-way answer. Throughout the client, `compare(catalog, installed) > 0`
means "an update is needed"; `0` or `-1` means "no update". Equal therefore means no action.

Requirements expressed with `minimum_os_version` and `maximum_os_version` are evaluated
against the Windows version with a separate, Windows-aware comparison and do not follow the
rules below.

## The algorithm

### 1. Short circuits

- Two byte-identical strings are equal. This happens before any normalisation, so
  `Setup` equals `Setup`.
- An empty or missing left-hand version sorts **below** a non-empty one. An empty
  right-hand version sorts **above** a non-empty left-hand one. A missing installed version
  therefore reads as "older than the catalog" and triggers an install.

### 2. Normalisation

Both sides are normalised independently:

1. **Parenthesis stripping.** Everything from the first `(` onward is removed, along with
   surrounding whitespace. `5.2.3 (git 68d178c)` becomes `5.2.3`.
2. **Comma to dot.** A comma with optional following whitespace becomes a dot.
   `2025, 0, 408, 54890` becomes `2025.0.408.54890`. This is the form the Windows file
   version resource is commonly written in.
3. **Splitting.** The string is split on `.`, `-` and `_`. Empty segments are dropped.
4. **Segment filtering, left to right.** A segment is kept if it parses as a 32-bit integer,
   or if it begins with `alpha`, `beta`, `rc` or `release`. **The first segment that is
   neither stops parsing entirely**, and everything from there rightward is discarded as
   trailing metadata. `1.2.3-build77` becomes `1.2.3`.
5. **Empty result means unparseable.** If no segment survived, the string has no normalised
   form at all.
6. **Build-timestamp expansion.** See below.

### 3. Unparseable versions compare equal

**If either side fails to normalise, the comparison returns equal.** It does not error, and
it does not favour either side.

Because equal means "no update", an item whose installed version cannot be parsed is
permanently current and will never upgrade again. The most common way to land here is a
first segment that is not a number:

- `v1.2.3` — the leading `v` makes the first segment `v1`, which is not an integer and not a
  pre-release tag, so parsing stops at segment one and nothing survives.
- `Example App 4.2` — the first segment is a word.
- `Setup`, `InternalName`, `NA`, `unknown` — whatever a version script or a file version
  resource prints when it does not actually know.

This is a deliberate trade: an unparseable string is treated as no evidence rather than as
evidence of being out of date, so a package does not reinstall on every run because a vendor
put a build tag in its version resource. The cost is silence in the other direction. If an
item never upgrades and you cannot see why, print both version strings and check whether
either one starts with a non-number.

### 4. Build-timestamp expansion

Packages built by `cimipkg` carry a build timestamp as their version, and Windows Installer
constrains an MSI `ProductVersion` to narrow field widths, so the same timestamp exists in
two encodings: the full `YYYY.MM.DD.HHMM` and the compressed three-part `yy.M.ddHH`.
Compared element-wise, the compressed form always loses to the full form regardless of which
build is newer.

A three-part version is therefore expanded to the full form when it can only be a timestamp.
All of these must hold:

- exactly three segments after normalisation
- first segment is an integer from 20 to 99
- second segment is an integer from 1 to 12
- third segment is 3 or 4 digits, and splits as a day of 1 to 31 and an hour of 0 to 23

`26.8.1712` then becomes `2026.08.17.1200`, which orders correctly against
`2026.04.28.1212`.

**This rule is unconditional and does not know what your product is.** A genuine three-part
product version that happens to fit the shape is rewritten too. `24.1.1000` is read as
10 January 2024 and becomes `2024.01.10.0000`. Within a family of similarly shaped versions
the mapping is monotonic and the ordering survives, but a mixed family does not:
`21.1.300` expands to `2021.01.03.0000` while `21.2.1` does not expand at all, so the client
concludes that `21.1.300` is the newer of the two. If your product uses three-part versions
where the third part runs to three or four digits, expect this and pick a different version
string — see below.

### 5. Element-wise comparison

The normalised strings are split on `.` and compared segment by segment. Missing segments on
either side are treated as `0`, so `1.2` and `1.2.0` are equal and `1.2.3` is greater than
`1.2`.

Each segment becomes a number:

| Segment | Value |
|---|---|
| an integer | that integer |
| begins with `alpha` | -3 |
| begins with `beta` | -2 |
| begins with `rc` | -1 |
| begins with `release` | 0 |

So `2.0-alpha` < `2.0-beta` < `2.0-rc` < `2.0`. Any digits attached to a tag are ignored —
`rc1` and `rc9` are both -1, and therefore equal.

The first segment that differs decides the result. If all segments are equal, the versions
are equal.

## Example comparisons

Read each row as: how does A order against B.

| A | B | Result | Why |
|---|---|---|---|
| `1.2.3` | `1.2.4` | A is older | third segment 3 < 4 |
| `1.2` | `1.2.0` | equal | missing segments are zero |
| `1.2.3` | `1.2` | A is newer | 3 > implied 0 |
| `5.2.3 (git 68d178c)` | `5.2.3` | equal | parenthesis stripped |
| `2025, 0, 408, 54890` | `2025.0.408.54890` | equal | commas become dots |
| `1.2.3-build77` | `1.2.3` | equal | `build77` stops parsing and is dropped |
| `26.8.1712` | `2026.04.28.1212` | A is newer | expanded to `2026.08.17.1200` |
| `2.0-beta` | `2.0` | A is older | beta is -2, missing segment is 0 |
| `2.0-rc1` | `2.0-beta2` | A is newer | rc (-1) beats beta (-2) |
| `2.0-rc1` | `2.0-rc9` | equal | digits after a tag are ignored |
| `1.0.0-alpha` | `1.0.0-rc` | A is older | alpha (-3) below rc (-1) |
| `v1.2.3` | `1.2.3` | equal | A is unparseable, so no update |
| `Setup` | `1.0.0` | equal | A is unparseable, so no update |
| *(empty)* | `1.0.0` | A is older | empty sorts below |
| `1.0.0` | *(empty)* | A is newer | empty sorts below |
| `21.1.300` | `21.2.1` | A is newer | timestamp expansion applied to A only |
| `1.2.99999999999` | `1.2.5` | A is older | the oversized segment stops parsing, leaving `1.2` |

## Choosing version strings that sort correctly

The version in a pkgsinfo is not a label. It is the value the client will order against
whatever the machine reports, so it has to be comparable with that value.

**Start with a digit.** No `v` prefix, no product name, no leading word. This is the single
most common cause of an item that never updates.

**Use dot-separated integers.** Two to four segments. Anything the vendor puts after a `-`
or `_` is discarded, so do not rely on it to distinguish two releases.

**Match the shape of what will be compared against it.** If detection reads an MSI
`DisplayVersion` of `4.2.1.0`, do not write `4.2.1` in the pkgsinfo and expect them to
differ — they compare equal. Conversely, if the vendor's file version resource is
`4.2.1.4799` and you write `4.2.1`, the machine will always look newer and the package will
never upgrade.

**Never write a version the target cannot report.** A version floor is only as precise as
the value it is compared against, after normalisation. If the vendor stamps
`27.6.0.11 (24.5.0)` into the file version resource, the parenthetical is stripped and only
`27.6.0.11` is comparable; a pkgsinfo version of `27.6.0.11.24` can never be satisfied and
the package reinstalls forever.

**Avoid three-part versions whose third segment is three or four digits** when the first
segment is between 20 and 99, unless it really is a `yy.M.ddHH` build stamp. Add a fourth
segment, or pad differently, so the timestamp rule does not fire.

**Do not encode meaning in pre-release digits.** `rc1` and `rc12` are indistinguishable.
Use a numeric segment if you need to order pre-releases.

**Keep every segment within a 32-bit integer.** A larger segment stops parsing, silently
truncating the version to whatever came before it.

**When there is no usable version at all**, do not invent one for detection to compare
against. Pin the payload with a checksum in an `installs` entry instead — see
[Installs Arrays](Installs-Arrays).

## See also

- [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Installs Arrays](Installs-Arrays)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [Using Catalogs](Using-Catalogs)
- [Install Loop Prevention](Install-Loop-Prevention)
- [Troubleshooting](Troubleshooting)
