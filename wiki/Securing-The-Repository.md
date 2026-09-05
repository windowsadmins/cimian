# Securing The Repository

Cimian's repository is a static web tree, so securing it is a matter of transport, of who is
allowed to read it, and of who is allowed to write to it. This page covers what an
unauthenticated repository actually exposes, the difference HTTPS makes, every authentication
mechanism the client implements and the exact order in which it chooses between them, mutual TLS
with client certificates, and the mechanisms Cimian does **not** support — several of which
appear in other deployment systems and are worth ruling out explicitly.

Everything here is client-side configuration in
`C:\ProgramData\ManagedInstalls\Config.yaml`. The server side is whatever your web server
offers; Cimian requires no server-side application. See
[The Cimian Repository](The-Cimian-Repository) for what the serving layer has to get right, and
[Client Configuration](Client-Configuration) for the complete key list.

## What an unauthenticated repository exposes

An open repository is readable by anyone who can reach it, and the four things it serves are
each worth thinking about separately.

**The catalogs are the most sensitive artefact.** A catalog is a full copy of every pkgsinfo
body assigned to it: every software title you deploy, its exact version, its installer's file
name and hash, and — because scripts in a pkgsinfo are inline strings rather than paths —
the complete text of every preinstall, postinstall, installcheck and uninstall script. Anything
embedded in those scripts is published with them. A catalog therefore discloses your software
estate, your patch level, and any secret an author put in a script body.

**The manifests disclose your fleet's shape.** Manifest file names are device identifiers by
design: the client asks for a manifest named after its client identifier, its machine name or
its BIOS serial. Because the names are predictable and the tree is fetched by direct GET, an
unauthenticated repository lets anyone enumerate which machines exist and read exactly what each
one is assigned.

**The payloads are your installers**, including any you are not licensed to redistribute.

**Write access is the serious one.** Anyone who can write to `pkgs\` or `catalogs\` can execute
code as SYSTEM on every managed machine, because that is precisely what the client is built to
do. Payload integrity is checked with the SHA-256 in `installer.hash` — but that hash comes from
the catalog, so an attacker who can rewrite both a payload and its catalog entry defeats the
check. Read access controls disclosure; write access controls the fleet.

Cimian mitigates none of this on its own. It sends no `Authorization` header at all unless you
configure one.

## HTTP versus HTTPS

The client accepts `http` and `https` schemes and rejects every other one, so `file://`, UNC
paths and object-storage-native schemes cannot be used at all.

Over plain HTTP, everything above travels in clear text, and so does the credential: a Basic
credential or a bearer token on an HTTP URL is transmitted in a header anyone on the path can
read. There is also no server authentication, so a client on a hostile network can be pointed at
a substituted repository, and a substituted catalog carries substituted script bodies.

Use HTTPS for anything beyond a local test. HTTP is reasonable for the single-machine
walkthrough in [Getting Started](Getting-Started) and for nothing else.

Cimian applies no TLS pinning and no protocol floor of its own; the .NET HTTP stack's defaults
apply, and the machine's own certificate store is the trust root unless you configure a custom
CA below.

## Authentication: one header, three sources

Cimian sets at most **one** `Authorization` header, chosen when the HTTP client is constructed.
The three sources are mutually exclusive and evaluated in a fixed order — the first one present
wins and the others are never consulted:

1. A DPAPI-protected Basic credential in the registry
2. `AuthToken` from `Config.yaml`, sent as a bearer token
3. `AuthUser` and `AuthPassword` from `Config.yaml`, sent as plaintext-derived Basic

There is no way to change that order, no per-URL rule and no fallback if the chosen credential
is rejected. The same header goes on every request the client makes: manifests, catalogs, icons
and payloads alike.

### 1. Basic credential in the registry, protected with DPAPI

The client reads the value `AuthHeader` under `HKLM\SOFTWARE\Cimian`. It expects a
Base64-encoded string, which it decodes and then decrypts with DPAPI at **machine scope**. The
plaintext is the Base64 `user:password` portion of a Basic credential; a leading `Basic ` is
tolerated and stripped, as are null characters and line breaks. The result is sent as
`Authorization: Basic <value>`.

Anything that fails — the key missing, the value missing, bad Base64, a failed decrypt — is
swallowed and treated as "no credential", and the client falls through to the next source.
There is no error and no log line naming the cause, so a broken blob looks identical to an
unconfigured one.

**No Cimian tool writes this value.** You create it yourself. Building the blob on the machine
that will use it is not optional: DPAPI at machine scope produces ciphertext that only that
machine can decrypt, so this credential cannot be authored centrally and copied to a fleet. Each
machine has to run something equivalent to this, elevated:

```powershell
$credential = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('repo-reader:REPLACE_WITH_PASSWORD'))
$protected  = [Security.Cryptography.ProtectedData]::Protect(
    [Text.Encoding]::UTF8.GetBytes($credential), $null, 'LocalMachine')
New-Item -Path HKLM:\SOFTWARE\Cimian -Force | Out-Null
New-ItemProperty -Path HKLM:\SOFTWARE\Cimian -Name AuthHeader `
    -Value ([Convert]::ToBase64String($protected)) -PropertyType String -Force
```

This is the strongest of the three header mechanisms, because the credential is not readable
from any file and not recoverable from the registry value alone. It is still a shared secret
that a local administrator on that machine can decrypt, since machine-scope DPAPI is
machine-bound rather than account-bound.

This value cannot be set from `Config.yaml` and cannot be delivered by the MDM policy key —
[Configuring Clients With Intune](Configuring-Clients-With-Intune) covers what policy can and
cannot carry.

### 2. Bearer token

```yaml
SoftwareRepoURL: https://cimian.example.com/repo
AuthToken: REPLACE_WITH_TOKEN
```

Sent as `Authorization: Bearer <token>` on every request. The token is stored in plain text in
`Config.yaml` and read by whoever can read that file, so protect the file's ACL and prefer a
token that is scoped to read-only access to this one path and can be revoked on its own.

Cimian does not refresh, renew or rotate a token, and treats a rejection as an ordinary HTTP
failure.

### 3. Plaintext Basic

```yaml
SoftwareRepoURL: https://cimian.example.com/repo
AuthUser: repo-reader
AuthPassword: REPLACE_WITH_PASSWORD
```

Both keys must be non-empty or the pair is ignored entirely. The client Base64-encodes
`user:password` at request time and sends `Authorization: Basic <value>`. The password sits in
clear text in a file on every managed machine — this is the weakest option, and the DPAPI
mechanism above exists to replace it.

### Confirming which one is in use

```powershell
managedsoftwareupdate --show-config
```

An HTTP 401 or 403 from the repository means the header the client sent was rejected, or none
was sent. Note the failure mode this produces: when a manifest request fails with anything other
than a 404, manifest resolution **aborts** rather than falling through to the next candidate
name, and the device gets no managed items for that run. A broken credential therefore leaves a
machine quietly unmanaged rather than dropping it onto a catch-all manifest — see
[Client Identifier Resolution](Client-Identifier-Resolution).

## Mutual TLS with client certificates

Client certificates are **independent of, and additive to**, the header cascade. They are
attached to the HTTP handler before any header is chosen, so a device can present a certificate
and a Basic credential at the same time. Enabling mTLS does not disable the header logic.

Set `UseClientCertificate: true` and supply the certificate one of three ways.

**A PFX or P12 file**, with an optional password:

```yaml
UseClientCertificate: true
ClientCertificatePath: C:\ProgramData\ManagedInstalls\client.pfx
ClientCertificatePassword: REPLACE_WITH_PASSWORD
```

**A PEM certificate with a separate key file**, which is the shape Munki uses. This is selected
by the certificate file's extension — `.pem`, `.crt` or `.cer`. `ClientKeyPath` is required in
this form; without it the certificate is not loaded:

```yaml
UseClientCertificate: true
ClientCertificatePath: C:\ProgramData\ManagedInstalls\client.pem
ClientKeyPath: C:\ProgramData\ManagedInstalls\client.key
```

**A certificate already in the Windows certificate store**, found by thumbprint. The personal
store is searched in `LocalMachine` first, then `CurrentUser`, and the match ignores whether the
certificate is currently valid. This is the option that avoids a private key on disk:

```yaml
UseClientCertificate: true
ClientCertificateThumbprint: 0123456789ABCDEF0123456789ABCDEF01234567
```

Every failure to load a certificate is a warning, not an error. The run continues without it,
which usually surfaces as a TLS handshake rejection or a 403 from the server rather than as a
clear message about the certificate.

### Using the certificate's CN as the manifest name

```yaml
UseClientCertificate: true
ClientCertificateThumbprint: 0123456789ABCDEF0123456789ABCDEF01234567
UseClientCertificateCNAsClientIdentifier: true
```

With both keys set, the certificate's common name becomes the **first** candidate the client
tries when resolving its primary manifest, ahead of `ClientIdentifier`, the machine name and the
BIOS serial. The CN is URL-escaped before it is used as a path segment.

This is the strongest device identity Cimian offers: a machine can only claim the manifest it
holds a certificate for, provided the server actually enforces the certificate. Keep the CN a
plain identifier — the escaping makes an awkward CN produce a manifest URL nobody expects.

## Trusting a private certificate authority

When the repository's TLS certificate chains to a CA the machine does not already trust, point
Cimian at the CA certificate rather than disabling validation:

```yaml
SoftwareRepoURL: https://cimian.example.com/repo
SoftwareRepoCACertificate: C:\ProgramData\ManagedInstalls\repo-ca.crt
```

This installs a validation callback that performs **real chain validation**. It is not a
blind-accept switch, and there is no blind-accept switch anywhere in Cimian:

- A certificate that already validates normally is accepted unchanged.
- Any error other than a chain-trust error — a host name mismatch, for instance — is rejected
  outright. The custom CA does not excuse it.
- A chain-trust error is retried once against a chain built with your CA as the sole custom
  trust root. If it builds, the certificate is accepted.

Revocation is not checked on that second pass. If the CA file cannot be read or parsed, the
callback is not installed at all and validation falls back to the machine's normal trust — a
warning is logged, and the run continues.

## What Cimian does not support

Each of these was checked in source and is absent. None of them can be worked around from
configuration.

| Not supported | Consequence |
|---|---|
| Shared-access-signature tokens and other signed-URL schemes | A repository behind a SAS-only endpoint cannot be used. Object storage works only when fronted by something that serves plain GETs at the repository's path layout. |
| Arbitrary or extra request headers | There is no `AdditionalHttpHeaders` key and no hook. A repository or gateway that requires a custom header cannot be reached. |
| Proxy configuration | The client sets no proxy. Requests use whatever the .NET HTTP stack does by default; there is no key to name a proxy, credentials or a bypass list. A proxy model exists in an unused configuration class and is not wired to anything. |
| Windows Integrated authentication (NTLM, Negotiate, Kerberos) | A repository secured with Windows authentication cannot be reached. |
| More than one `Authorization` header source at a time | The cascade picks one. A bearer token is never tried when the registry credential is present, even if the registry credential is rejected. |
| Retry on an authentication failure | Manifest and catalog fetches are a single request each with no retry. Only payload downloads retry. |
| Per-path or per-content-type credentials | The same header is sent to manifests, catalogs, icons and payloads. |

## Protecting the payloads and the metadata

The metadata and the payloads have different exposure and can reasonably be protected
differently, but only within one constraint: **every request carries the same credential**, so
they must both be readable by whatever identity the client presents. You cannot give the client
one credential for `catalogs\` and a different one for `pkgs\`.

What you can do is split them by URL. Payload locations are the only part of the tree that may
point elsewhere: when an item's `installer.location` already begins with `http://` or `https://`
it is used verbatim instead of being appended under `{SoftwareRepoURL}/pkgs`. That lets payloads
live on a separate host — a content delivery network, or storage closer to the client — while
the metadata stays on the authenticated origin. The client sends the same `Authorization` header
to that host too, so the alternative host must either accept it or not require it.

Whatever the layout, four things hold.

**Turn directory listing off.** The client never lists a directory, never asks for an index and
never walks the tree; it requests exact paths only. Listing gives an attacker your manifest
names for free and gives the client nothing.

**Keep write access much narrower than read access.** Publishing should be done by an identity
distinct from the one clients read with. The clients need `GET` and nothing else.

**Do not put secrets in a pkgsinfo script body.** They are copied verbatim into the catalog and
served to every device that reads it. Where a package genuinely needs a secret at build time,
`cimipkg`'s `${VAR}` substitution from a `.env` file keeps it out of the repository — see
[cimipkg](cimipkg) and [Scripts In pkgsinfo](Scripts-In-pkgsinfo).

**Publish a new version at a new path.** A payload is verified against the SHA-256 in the
catalog after download and again before a cached copy is reused, so replacing the bytes at an
existing path without changing the pkgsinfo makes every client fail that item. That behaviour is
also the integrity guarantee: as long as the catalog is trustworthy, a substituted payload is
rejected.

## See also

- [The Cimian Repository](The-Cimian-Repository)
- [Client Configuration](Client-Configuration)
- [Client Identifier Resolution](Client-Identifier-Resolution)
- [Configuring Clients With Intune](Configuring-Clients-With-Intune)
- [Using Catalogs](Using-Catalogs)
- [Cimian With Git](Cimian-With-Git)
- [The Download Cache](The-Download-Cache)
- [Troubleshooting](Troubleshooting)
