# Product Icons And Screenshots

Icons are what make [Managed Software Center](Managed-Software-Center) look like a software
portal instead of a list of coloured squares. This page covers where icons live in the
repository, how a device gets them, how `icon_name` resolves, what happens when an icon is
missing, and how to supply icons in bulk. It also covers screenshots, which are not supported
end to end today.

## Where icons live

Icons are served from the repository, in a flat directory beside `pkgsinfo` and `pkgs`:

```
<repo>/icons/ExampleApp.png
<repo>/icons/ExampleUtility.png
```

They are fetched over the same base URL as everything else the client downloads —
`https://cimian.example.com/repo/icons/<filename>` — so whatever serves your catalogs must
also serve this directory.

The directory is flat. The client refuses any icon filename containing `/`, `\` or `..`, so
subdirectories such as `icons/design/ExampleApp.png` can never be reached.

## How a device gets them

At the end of every check, `managedsoftwareupdate` mirrors the icons for the items on that
device's manifest down to:

```
C:\ProgramData\ManagedInstalls\icons\
```

The sync is deliberately cheap and deliberately harmless. It uses conditional requests, so a
steady-state run costs one "not modified" response per icon; it downloads at most four at a
time; and it never fails a run. An icon that is not in the repository comes back as a 404 and
is skipped silently, and any other failure is logged and swallowed.

MSC reads only the local directory. It never contacts the repository itself, so a device that
has not completed a check since you added an icon will not show it yet.

## `icon_name` and the naming convention

`icon_name` is a pkgsinfo key holding a bare filename:

```yaml
name: ExampleApp
display_name: Example App
version: 3.2.1
icon_name: ExampleApp.png
catalogs:
  - Production
installer:
  location: apps/ExampleApp-3.2.1.msi
  type: msi
```

When `icon_name` is not set, the client looks for `<name>.png` — the item's `name`, not its
`display_name`, with a `.png` extension. **This is the convention to follow**: name the file
after the item and omit the key. An icon for an item named `ExampleApp` is
`<repo>/icons/ExampleApp.png` and needs no pkgsinfo change at all.

Set `icon_name` when the file cannot be named after the item — a shared icon used by several
related packages, or a suite whose components should all show the same artwork:

```yaml
name: ExampleDesignSuite-Photo
display_name: Example Photo
version: 2026.1
icon_name: ExampleDesignSuite.png
catalogs:
  - Production
```

Give `icon_name` a complete filename including the extension. The mirror fetches the string
verbatim, so `icon_name: ExampleApp` requests a file literally called `ExampleApp` with no
extension, and unless such a file exists in the repository nothing is downloaded.

## Formats and sizes

MSC will render a local file with any of these extensions:

```
.png  .jpg  .jpeg  .ico  .bmp
```

The mirror, though, only ever downloads the exact filename it resolved — `icon_name` if set,
otherwise `<name>.png`. So a JPEG is only usable if you name it in `icon_name`; a file called
`ExampleApp.jpg` with no `icon_name` is never fetched, even though MSC would display it if it
were already on the device.

Use PNG with transparency. No size is enforced, and the tiles scale whatever you supply, so
supply something square and large enough not to blur — 256×256 is a good default and matches
what icon extraction produces.

## When an icon is missing

Nothing breaks. MSC generates a plain 64×64 solid-colour tile, with the colour chosen
deterministically from the item's name out of a fixed ten-colour palette. The tile carries no
initials and no glyph; the item's name is shown beneath it by the card itself.

A grid of solid-coloured squares therefore means one of four things, in rough order of
likelihood:

1. No icon has been published for those items.
2. `icon_name` names a file that is not in `<repo>/icons/`, or has the wrong extension.
3. The device has not completed a check since the icons were added.
4. The repository's `icons/` directory is not being served — check that
   `https://cimian.example.com/repo/icons/ExampleApp.png` returns the file for a client with
   the same credentials the device uses.

MSC caches each item's icon in memory for the life of the process, keyed by item name, so
after replacing an icon on a device you need to restart MSC to see the new one.

## Supplying icons in bulk

The straightforward path is to name every file after its item and drop the lot into the
repository:

```
<repo>/icons/ExampleApp.png
<repo>/icons/ExampleUtility.png
<repo>/icons/ExampleDesignSuite.png
```

No pkgsinfo edits and no `makecatalogs` run are needed — the icon mirror reads item names,
not the catalog, and picks up new files on the next check. Publishing icons is a pure content
change.

If your repository is a git checkout, watch out for a `.gitignore` rule covering `*.png`;
that is a common reason a directory full of icons never reaches the server.

`cimiimport` can extract an icon from the installer at import time. It is off by default and
marked experimental:

```
cimiimport C:\Downloads\ExampleApp-3.2.1.msi --extract-icon
```

It writes `<repo>/icons/<name>.png` and sets `icon_name` in the pkgsinfo it creates. Use
`--icon <path>` to write the extracted file somewhere else. Extraction failures are not fatal
to the import, so check the result rather than assuming an icon appeared. `makepkginfo` does
not write `icon_name` at all — add it by hand when you need it.

To audit coverage, list the item names in a catalog against the files in `icons/`; any item
whose `<name>.png` is absent and which has no `icon_name` will render as a coloured tile.

## Screenshots and rich descriptions

**Screenshots are not supported.** MSC's item detail page has a screenshot gallery, and it
reads a `screenshots` list from the per-item data the client publishes, but the client never
writes that list — there is no pkgsinfo key that feeds it. The gallery is always hidden.

**Release notes are not supported either.** The "What's New" panel is likewise driven by data
the client does not publish, so it never appears.

The rich text a user does see is `description`, which is the one place to describe an item:

```yaml
name: ExampleApp
display_name: Example App
version: 3.2.1
description: |
  Example App is the standard tool for editing project files.

  Licensed for staff use. Contact the service desk if you need a licence key.
catalogs:
  - Production
```

Use a YAML block scalar for multi-paragraph text. Line endings are normalised and runs of
three or more blank lines are collapsed when the catalog is built. An empty `description` is
dropped entirely when a pkgsinfo is rewritten, so leave the key out rather than setting it to
an empty string.

## Related client-side artwork

Two other image locations affect the look of MSC, and neither is synced from the repository —
put the files on the device with your management tooling:

- `C:\ProgramData\ManagedInstalls\branding\branding*.png|jpg|jpeg` — up to three images that
  cross-fade in the hero banner on the Software page.
- `C:\ProgramData\ManagedInstalls\client_resources\` — the sidebar header image named by
  `branding.yaml`.

## Limitations

- No screenshots and no release notes reach the UI.
- The `icons/` directory is flat; subdirectories cannot be referenced.
- Without `icon_name`, only `<name>.png` is ever fetched — other formats need an explicit
  `icon_name`.
- The local icons directory is never pruned. Icons for items a device no longer receives stay
  on disk.
- MSC caches icons per process, so a replaced icon needs an application restart to appear.
- Icon sync is silent by design: a missing icon is not a warning and not an error anywhere in
  the run output.

## See also

- [Managed Software Center](Managed-Software-Center)
- [Optional Installs And Self Service](Optional-Installs-And-Self-Service)
- [Featured Items](Featured-Items)
- [Supported pkgsinfo Keys](Supported-pkgsinfo-Keys)
- [The Cimian Repository](The-Cimian-Repository)
- [cimiimport](cimiimport)
