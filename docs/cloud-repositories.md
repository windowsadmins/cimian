# Hosting a Cimian repository over HTTPS

Cimian's client fetches repository content through `HttpClient`. A repository can live on a web server, behind a gateway, or in cloud object storage served over HTTPS. The client does not need the storage provider's SDK or cloud account credentials to download it.

This is the deployment approach proposed in [issue #67](https://github.com/windowsadmins/cimian/issues/67). It describes an operator-managed service, not infrastructure provisioned by Cimian.

## Choose a serving pattern

| Pattern | How requests reach the files | When to use it |
| --- | --- | --- |
| Static HTTPS origin or CDN | The repository URL serves the expected catalogs, manifests and payload paths directly. | Public content, or a network boundary that already controls access. |
| Authenticating reverse proxy | A gateway authenticates the client, authorizes the requested path and streams the origin response. | Private repositories that need one stable hostname or must keep identifying headers at the gateway. |
| Gateway with a short-lived signed redirect | A gateway authorizes a request and returns a temporary HTTPS redirect to one signed object URL. | Large payloads that should download directly from object storage. |

Keep the repository's existing relative paths, including YAML catalog and manifest names and each payload's configured location. A cloud endpoint does not select a different repository format. Plist support is a separate proposal.

## Configure the client for the gateway

Point `SoftwareRepoURL` at the HTTPS origin or gateway. Use the client's existing authentication settings: repository bearer/basic authentication or `UseClientCertificate` with a certificate file or `ClientCertificateThumbprint`. Protect any configured credentials through the deployment system used by your organization.

`AdditionalHttpHeaders` can carry identifiers expected by a repository server. `AdditionalHttpHeadersHttpsOnly: true` limits credentials and configured additional headers to initial requests over HTTPS; it does not restrict them to one hostname. A thumbprint selects one exact certificate. Issuer, subject-pattern and certificate-template selection are not implemented configuration options.

Keep cloud storage credentials on the server. Deploy the gateway, its identity, storage permissions and network policy through the operator's infrastructure-as-code system. Prefer read-only access scoped to the repository and authorize a requested object before issuing a signed URL.

## Signed redirect behavior

Use a temporary redirect such as HTTP 302 or 307 for each authorized object request. Do not publish a permanent redirect to an expiring signed URL. Keep the destination HTTPS and give the URL enough lifetime for the expected transfer and retry window. A signed URL is a bearer credential: prevent it from appearing in public logs or analytics.

The .NET handler follows redirects by default, clears `Authorization` when following one, and does not follow an HTTPS-to-HTTP downgrade. It does not generally clear every custom header. `AdditionalHttpHeadersHttpsOnly` runs outside that redirect handling and does not establish a destination allowlist. Do not assume that additional identifying or secret headers stop at the first host. Use a reverse proxy when those values must remain at the gateway, or constrain redirects to destinations allowed to receive them.

Do not require the original repository authorization header at the storage destination. Its signed query string authorizes the object request. Account for caching: device-specific manifests and signed redirects must not be reused for a different device, and cached redirects must not outlive their signed URL.

Azure Blob SAS and S3 presigned URLs both provide this limited-access URL pattern. The gateway and its authorization policy remain the operator's responsibility. This document does not provide or claim a tested reference gateway deployment.

## Validate an operator deployment

Use a test repository and a test device before changing the fleet's repository URL:

1. Check catalog, manifest and payload requests through the configured URL, with expected authentication and path encoding.
2. Confirm an unauthorized device or path is denied, including traversal and attempts to fetch another device's manifest.
3. Confirm payload hashes match and the expected authentication material does not reach an unintended redirect destination.
4. Exercise signed-URL expiry, a retried download, and any range/resume behavior required by the origin and client.
5. Run `managedsoftwareupdate.exe --checkonly` under the account that normally runs it, including SYSTEM when applicable. Confirm its catalog and manifest results before installing packages.

## Contribution boundary

Unused AWS S3 and Azure Blob package references can be removed without adding a cloud backend to the client. A native SDK should be introduced only for a concrete API operation that HTTPS repository downloads cannot perform, with its own implementation, footprint measurement and tests.

Runtime-loaded middleware is a separate design decision. It is not needed for the server-side patterns above and is not supported by the current client. Any plugin proposal must define its trust/elevation boundary, update compatibility, dependency loading and interaction with trimming before implementation.

## References

- [HttpClientHandler redirect behavior](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.allowautoredirect?view=net-10.0)
- [Azure shared access signatures](https://learn.microsoft.com/en-us/azure/storage/common/storage-sas-overview)
- [S3 presigned URLs](https://docs.aws.amazon.com/AmazonS3/latest/userguide/using-presigned-url.html)
