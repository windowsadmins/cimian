# Branding Managed Software Center

Managed Software Center and the CimianStatus progress window can carry your
organisation's name, colour, logo, icon and banners. Branding arrives in a
`client_resources` zip on the software repo, as in Munki, and any of it can be
overridden by machine policy.

## How the zip is found

On each `managedsoftwareupdate` run, the client downloads one zip from the
client resources location. The location is `ClientResourceURL` from
`Config.yaml` when it is set, and `{SoftwareRepoURL}/client_resources`
otherwise. The names below are tried in order, and the first one the server
has wins:

1. `ClientResourcesFilename` from `Config.yaml`, with `.zip` added when it has
   no extension.
2. The certificate CN, when `UseClientCertificateCNAsClientIdentifier` is on.
3. `ClientIdentifier`.
4. The computer name.
5. `site_default`.

`ClientResourceURL` and `ClientResourcesFilename` work as Munki's settings of
the same names.

Conditional GET (`If-None-Match` / `If-Modified-Since`) avoids re-extracting an
unchanged zip. State, including the list of files the zip put down, is kept in
`C:\ProgramData\ManagedInstalls\client_resources\.repo-sync.json`.

When a new zip no longer has a file the previous one had, that file is deleted,
so a banner you take out of the zip disappears from the client. When none of the
names exists on the server any more, every file the last zip put down is
removed, as Munki removes its `custom.zip`. A file that has changed since it was
extracted is left alone.

Failures are logged and never fail the update run.

## Zip layout

| Archive path                       | Lands in                                                         |
| ---------------------------------- | ---------------------------------------------------------------- |
| `branding.yaml`                    | `%ProgramData%\ManagedInstalls\client_resources\`                |
| `branding/<file>`                  | `%ProgramData%\ManagedInstalls\branding\`                        |
| `client_resources/<file>`          | `%ProgramData%\ManagedInstalls\client_resources\`                |
| `preferences.yaml`                 | `%ProgramData%\ManagedInstalls\preferences.yaml`                 |
| `resources/branding*.png` / `.jpg` | `%ProgramData%\ManagedInstalls\branding\` (Munki layout)         |
| `resources/<other file>`           | `%ProgramData%\ManagedInstalls\client_resources\` (Munki layout) |

Munki's `templates/` HTML is skipped: Managed Software Center is a native
Windows app, not a web view, so there are no page templates to override. Its
banners, banner links, sidebar and Help link are covered by the settings below.

## branding.yaml

Every key is optional. File names are bare names, looked up in `branding\` and
then `client_resources\`. Paths are refused, so branding can only show files an
administrator put in those folders.

```yaml
app_title: Unchained Software
banner_title: Get the tools you need
accent_color: "#0B5CAD"
logo: logo.png
app_icon: app.ico
sidebar_header: sidebar_header.png
banners:
  - image: branding-welcome.png
    href: munki://category-Productivity
  - image: branding-security.png
    href: https://intranet.example.org/security
  - image: branding-updates.png
    href: cimian://updates
```

| Key              | Effect                                                                                                                                                                                                                                                    |
| ---------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `app_title` | Window title, title bar and taskbar name in Managed Software Center, and the CimianStatus window title and heading. Also the hero heading when `banner_title` is unset and there are no banner images. |
| `banner_title` | Software page hero heading. An empty string hides it. Over banner images only an explicit `banner_title` is drawn, as Munki draws no text over its showcase. |
| `accent_color`   | Accent for buttons, links, selection, progress, the hero background and the Featured tile, as `#RRGGBB` (`#RGB` and `#AARRGGBB` are accepted; alpha is ignored). Text on accent buttons switches between white and black to stay readable. Unset follows the Windows accent. |
| `logo`           | PNG or JPEG shown in the Managed Software Center title bar and in place of the Cimian logo in CimianStatus. Keep it wide and short; it is drawn 20 px high in the title bar.                                                                              |
| `app_icon`       | `.ico` used for the window and taskbar icon of both apps.                                                                                                                                                                                                 |
| `sidebar_header` | Image above the sidebar. Defaults to `sidebar_header.png` or `sidebar_header.jpg` when present.                                                                                                                                                           |
| `banners`        | Software page carousel, in order, up to 10 slides. Without it, every `branding*.png` / `.jpg` / `.jpeg` in `branding\` is shown in name order, as Munki's default showcase does.                                                                          |

Slides cross-fade every 7.5 seconds, as in Munki; the dots under the banner jump
to a slide.

### Banner links

A slide with an `href` opens it when clicked:

| `href`                                                                                                | Opens                                                                     |
| ----------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------- |
| `cimian://item/<name>`                                                                                | Item detail page                                                          |
| `cimian://category/<name>`                                                                            | Software page filtered to that category                                   |
| `cimian://updatedetail/<name>`                                                                        | Update detail page                                                        |
| `cimian://updates`, `categories`, `myitems`, `history`, `software`                                    | That page                                                                 |
| `munki://detail-<name>`, `category-<name>`, `updatedetail-<name>`, `updates`, `categories`, `myitems` | The same pages, so links from a Munki `showcase_template.html` carry over |
| `http://…`, `https://…`                                                                               | Default browser                                                           |

Any other link, including `file:` and `javascript:`, is ignored and the slide is
not clickable. `munki://filter-…` and `munki://developer-…` open the Software
page, since Cimian has no filtered list pages yet.

## Policy overrides

Every branding key can be set by machine policy under
`HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Cimian`, as REG_SZ values. A policy value
that exists wins over `branding.yaml`. An empty value counts as unset, except
`BannerTitle`, where empty hides the hero heading.

| Policy value    | `branding.yaml` key                                    |
| --------------- | ------------------------------------------------------ |
| `AppTitle`      | `app_title`                                            |
| `BannerTitle`   | `banner_title`                                         |
| `AccentColor`   | `accent_color`                                         |
| `Logo`          | `logo` (a file name in the branding folders)           |
| `AppIcon`       | `app_icon` (a file name in the branding folders)       |
| `SidebarHeader` | `sidebar_header` (a file name in the branding folders) |

`HelpURL` and `CategoryIcons` are policy-backed too; see
[CSP OMA-URI configuration](csp-oma-uri-configuration.md).

A value that cannot be used, such as a colour that is not `#RRGGBB` or a logo
that is not in the branding folders, falls back to `branding.yaml` and then the
default, and is noted in Managed Software Center's startup log.

## What Munki has that Cimian does not

- HTML template overrides (`templates/*.html`), because Managed Software Center
  has no web view.
- `munki://filter-…` and `munki://developer-…` list pages.

## What Cimian has that Munki does not

Munki has no setting for the app name, accent colour or app icon; its admins
rebuild the app to change them. Cimian takes all three from `branding.yaml` or
policy.
