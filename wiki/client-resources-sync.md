# Client resources sync (MSC branding from the repo)

Cimian can download Managed Software Center branding from the software repo,
similar to Munki's `client_resources/site_default.zip`.

## URLs

On each `managedsoftwareupdate` run, the client tries (first hit wins):

1. `{SoftwareRepoURL}/client_resources/{ClientIdentifier}.zip`
2. `{SoftwareRepoURL}/client_resources/{MachineName}.zip` (if different)
3. `{SoftwareRepoURL}/client_resources/site_default.zip`

When `UseClientCertificateCNAsClientIdentifier` is enabled, the certificate CN
is tried before `ClientIdentifier`.

Conditional GET (`If-None-Match` / `If-Modified-Since`) avoids re-extracting
unchanged zips. State is stored in
`C:\ProgramData\ManagedInstalls\client_resources\.repo-sync.json`.

## Zip layout

| Archive path | Local path |
| ------------ | ---------- |
| `branding/branding.png` | `%ProgramData%\ManagedInstalls\branding\` |
| `client_resources/branding.yaml` | `%ProgramData%\ManagedInstalls\client_resources\` |
| `client_resources/sidebar_header.png` | same |
| `preferences.yaml` | `%ProgramData%\ManagedInstalls\preferences.yaml` |

Munki `resources/branding*.png|jpg` entries map into `branding\`.
`templates/` HTML is ignored.

Failures are logged only; they never fail the update run.
