using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core.Services;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Shared HTTP client factory with authentication and SSL client certificate support.
/// Consolidates the duplicated CreateHttpClient methods from DownloadService,
/// ManifestService, and CatalogService into a single implementation.
/// </summary>
public static class CimianHttpClientFactory
{
    /// <summary>
    /// Creates an HttpClient configured with authentication, optional client certificates and
    /// the AdditionalHttpHeaders setting. With AdditionalHttpHeadersHttpsOnly set, the
    /// credential and the additional headers go only on https requests.
    /// A certificate file is loaded once per process and shared by every client; see
    /// <see cref="SharedClientCertificate"/>.
    /// </summary>
    public static HttpClient CreateHttpClient(CimianConfig config, TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler();

        // SSL client certificate support
        if (config.UseClientCertificate)
        {
            var cert = LoadClientCertificate(config);
            if (cert != null)
            {
                handler.ClientCertificates.Add(cert);
                ConsoleLogger.Detail($"    SSL client certificate loaded: {cert.Subject}");
            }
        }

        // Custom CA certificate for server validation
        if (!string.IsNullOrEmpty(config.SoftwareRepoCACertificate))
        {
            var validator = CreateCustomCaValidator(config.SoftwareRepoCACertificate);
            if (validator != null)
            {
                handler.ServerCertificateCustomValidationCallback = validator;
                ConsoleLogger.Detail($"    Custom CA certificate loaded: {config.SoftwareRepoCACertificate}");
            }
        }

        var authorization = GetAuthorization(config);
        var additionalHeaders = ParseAdditionalHeaders(config);

        // https-only: the credential and additional headers are attached per request, and
        // only over https, so a plain http request (a local caching server on port 80, say)
        // never carries them.
        var httpsOnly = config.AdditionalHttpHeadersHttpsOnly
            && (authorization != null || additionalHeaders.Count > 0);
        HttpMessageHandler pipeline = httpsOnly
            ? new HttpsOnlyHeadersHandler(authorization, additionalHeaders, handler)
            : handler;

        var client = new HttpClient(pipeline)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(60)
        };

        client.DefaultRequestHeaders.Add("User-Agent", "Cimian-ManagedSoftwareUpdate/1.0");

        if (!httpsOnly)
        {
            client.DefaultRequestHeaders.Authorization = authorization;
            foreach (var (name, value) in additionalHeaders)
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
            }
        }

        return client;
    }

    /// <summary>
    /// The repository credential, or null when none is configured.
    /// Priority: DPAPI registry → Bearer token → Basic auth.
    /// </summary>
    internal static AuthenticationHeaderValue? GetAuthorization(CimianConfig config)
    {
        var authHeader = AuthService.GetAuthHeader();
        if (!string.IsNullOrEmpty(authHeader))
            return new AuthenticationHeaderValue("Basic", authHeader);

        if (!string.IsNullOrEmpty(config.AuthToken))
            return new AuthenticationHeaderValue("Bearer", config.AuthToken);

        if (!string.IsNullOrEmpty(config.AuthUser) && !string.IsNullOrEmpty(config.AuthPassword))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{config.AuthUser}:{config.AuthPassword}"));
            return new AuthenticationHeaderValue("Basic", credentials);
        }

        return null;
    }

    /// <summary>
    /// The usable AdditionalHttpHeaders entries, in order. An entry is split at its first
    /// colon, and a later entry for the same header replaces an earlier one. Authorization
    /// belongs to the auth settings above and User-Agent identifies the client, so an entry
    /// for either is skipped rather than replacing them. So is an entry with a control or
    /// non-ASCII character in its value: a line break or another control character would be
    /// written to the wire as it is, and .NET will not send a request that carries a
    /// non-ASCII one. Warnings give an entry's position, not its value, because a header
    /// value can be a secret.
    /// </summary>
    internal static List<KeyValuePair<string, string>> ParseAdditionalHeaders(CimianConfig config)
    {
        var result = new List<KeyValuePair<string, string>>();
        var entryNumber = 0;
        foreach (var entry in config.AdditionalHttpHeaders ?? [])
        {
            entryNumber++;
            if (!TrySplitHeader(entry, out var name, out var value))
            {
                ConsoleLogger.Warn($"Ignoring AdditionalHttpHeaders entry {entryNumber}: expected \"Name: value\"");
                continue;
            }

            if (HasInvalidCharacter(name) || HasInvalidCharacter(value))
            {
                ConsoleLogger.Warn($"Ignoring AdditionalHttpHeaders entry {entryNumber}: a name or value can hold only printable ASCII characters");
                continue;
            }

            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleLogger.Warn($"Ignoring AdditionalHttpHeaders entry {entryNumber}: {name} cannot be set here");
                continue;
            }

            using var probe = new HttpRequestMessage();
            if (!probe.Headers.TryAddWithoutValidation(name, value))
            {
                ConsoleLogger.Warn($"Ignoring AdditionalHttpHeaders entry {entryNumber}: not a valid request header name");
                continue;
            }

            result.RemoveAll(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            result.Add(new(name, value));
        }

        return result;
    }

    // Printable ASCII and tab only.
    private static bool HasInvalidCharacter(string text) => text.Any(c => c > '~' || (c < ' ' && c != '\t'));

    internal static bool TrySplitHeader(string? entry, out string name, out string value)
    {
        name = value = string.Empty;
        var colon = entry?.IndexOf(':') ?? -1;
        if (entry is null || colon < 0)
            return false;

        name = entry[..colon].Trim();
        value = entry[(colon + 1)..].Trim();
        return name.Length > 0;
    }

    /// <summary>
    /// Loads a client certificate from file (PEM or PFX) or Windows Certificate Store.
    /// PEM format uses separate cert + key files (Munki-compatible).
    /// PFX format uses a single file with optional password.
    /// </summary>
    private static X509Certificate2? LoadClientCertificate(CimianConfig config)
    {
        // Option 1: Certificate file on disk (PEM or PFX), loaded once per process
        if (!string.IsNullOrEmpty(config.ClientCertificatePath))
        {
            return SharedClientCertificate.Get(config);
        }

        // Option 2: Windows Certificate Store by thumbprint
        if (!string.IsNullOrEmpty(config.ClientCertificateThumbprint))
        {
            var thumbprint = config.ClientCertificateThumbprint.Replace(" ", "").ToUpperInvariant();

            // Search LocalMachine\My first, then CurrentUser\My
            foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
            {
                using var store = new X509Store(StoreName.My, location);
                try
                {
                    store.Open(OpenFlags.ReadOnly);
                    var certs = store.Certificates.Find(
                        X509FindType.FindByThumbprint, thumbprint, validOnly: false);

                    if (certs.Count > 0)
                    {
                        ConsoleLogger.Detail($"    Found client certificate in {location}\\My store");
                        return certs[0];
                    }
                }
                catch (Exception ex)
                {
                    ConsoleLogger.Detail($"    Could not search {location}\\My store: {ex.Message}");
                }
            }

            ConsoleLogger.Warn($"Client certificate with thumbprint {thumbprint} not found in any store");
        }

        return null;
    }

    /// <summary>
    /// Creates a server certificate validation callback that trusts a custom CA certificate.
    /// Performs real chain validation — does NOT blindly accept all certificates.
    /// </summary>
    private static Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>?
        CreateCustomCaValidator(string caCertPath)
    {
        if (!File.Exists(caCertPath))
        {
            ConsoleLogger.Warn($"Custom CA certificate file not found: {caCertPath}");
            return null;
        }

        X509Certificate2 caCert;
        try
        {
            caCert = X509CertificateLoader.LoadCertificateFromFile(caCertPath);
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Failed to load custom CA certificate from {caCertPath}: {ex.Message}");
            return null;
        }

        return (message, cert, chain, errors) =>
        {
            // No errors — the default trust chain is fine
            if (errors == SslPolicyErrors.None)
                return true;

            // Only handle untrusted root errors — reject other types (name mismatch, etc.)
            if ((errors & SslPolicyErrors.RemoteCertificateChainErrors) == 0)
                return false;

            if (cert == null || chain == null)
                return false;

            // Build a new chain with our custom CA as an extra trusted root
            using var customChain = new X509Chain();
            customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            customChain.ChainPolicy.ExtraStore.Add(caCert);
            customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            customChain.ChainPolicy.CustomTrustStore.Add(caCert);

            return customChain.Build(cert);
        };
    }

    /// <summary>
    /// Reads the certificate file named by ClientCertificatePath (PFX, or PEM with
    /// ClientKeyPath). Its private key is ephemeral and exportable: Schannel cannot use an
    /// ephemeral key for client authentication, so <see cref="SharedClientCertificate"/>
    /// moves it into a named machine key before the certificate is used.
    /// </summary>
    internal static X509Certificate2? ReadClientCertificateFile(CimianConfig config)
    {
        if (!File.Exists(config.ClientCertificatePath))
        {
            ConsoleLogger.Warn($"Client certificate file not found: {config.ClientCertificatePath}");
            return null;
        }

        var ext = Path.GetExtension(config.ClientCertificatePath).ToLowerInvariant();

        // PEM format — separate cert and key files (Munki-style)
        if (ext is ".pem" or ".crt" or ".cer")
        {
            return LoadPemCertificate(config);
        }

        // PFX/P12 format — cert and key in one file
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(
                config.ClientCertificatePath!,
                config.ClientCertificatePassword,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Failed to load client certificate from {config.ClientCertificatePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loads a PEM certificate with a separate private key file.
    /// This is the format Munki uses: client.pem + client.key.
    /// </summary>
    private static X509Certificate2? LoadPemCertificate(CimianConfig config)
    {
        if (string.IsNullOrEmpty(config.ClientKeyPath))
        {
            ConsoleLogger.Warn("PEM certificate requires ClientKeyPath to be set");
            return null;
        }

        if (!File.Exists(config.ClientKeyPath))
        {
            ConsoleLogger.Warn($"Client key file not found: {config.ClientKeyPath}");
            return null;
        }

        try
        {
            var certPem = File.ReadAllText(config.ClientCertificatePath!);
            var keyPem = File.ReadAllText(config.ClientKeyPath);
            return X509Certificate2.CreateFromPem(certPem, keyPem);
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"Failed to load PEM certificate from {config.ClientCertificatePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gets the CN from the client certificate for use as client identifier.
    /// Returns null if mTLS is not configured, the feature is disabled, or the cert can't be read.
    /// </summary>
    public static string? GetClientCertificateCN(CimianConfig config)
    {
        if (!config.UseClientCertificate || !config.UseClientCertificateCNAsClientIdentifier)
            return null;

        // Read cert metadata only — avoid loading private key just for CN
        X509Certificate2? cert = null;
        try
        {
            if (!string.IsNullOrEmpty(config.ClientCertificatePath) && File.Exists(config.ClientCertificatePath))
            {
                cert = X509CertificateLoader.LoadCertificateFromFile(config.ClientCertificatePath);
            }
            else if (!string.IsNullOrEmpty(config.ClientCertificateThumbprint))
            {
                cert = LoadClientCertificate(config);
            }
        }
        catch
        {
            return null;
        }

        if (cert == null)
            return null;

        var cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (string.IsNullOrEmpty(cn))
            return null;

        // Sanitize for use as URL path segment (manifest name)
        return Uri.EscapeDataString(cn);
    }
}
