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
    /// Key storage flags for PKCS#12 material used with HttpClientHandler client auth.
    /// EphemeralKeySet must not be used on Windows: Schannel cannot marshal in-memory
    /// private keys to LSASS, so TLS client auth fails even when HasPrivateKey is true.
    /// MachineKeySet keeps that key in the machine store, which is the right container
    /// for a process running as SYSTEM. PersistKeySet is intentionally omitted: the key
    /// is temporary and is deleted when the owning certificate is disposed.
    /// See https://github.com/dotnet/runtime/issues/23749 and
    /// https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-troubleshooting#handshake-failed-with-ephemeral-keys
    /// </summary>
    private static X509KeyStorageFlags ClientCertificateKeyStorageFlags =>
        OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.MachineKeySet
            : X509KeyStorageFlags.EphemeralKeySet;

    /// <summary>
    /// File-imported certificate plus whether this process created its private key.
    /// Store certificates stay owned by the certificate store.
    /// </summary>
    private readonly record struct LoadedClientCertificate(X509Certificate2 Certificate, bool DisposeWithClient);

    /// <summary>
    /// Creates an HttpClient configured with authentication and optional client certificates.
    /// Auth priority: DPAPI registry → Bearer token → Basic auth.
    /// Disposing the returned client also disposes a file-imported client certificate,
    /// which deletes its temporary key file.
    /// </summary>
    public static HttpClient CreateHttpClient(CimianConfig config, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var innerHandler = new HttpClientHandler();
        X509Certificate2? ownedCertificate = null;

        // SSL client certificate support
        if (config.UseClientCertificate)
        {
            var loaded = LoadClientCertificate(config);
            if (loaded is not null)
            {
                innerHandler.ClientCertificates.Add(loaded.Value.Certificate);
                ConsoleLogger.Detail($"    SSL client certificate loaded: {loaded.Value.Certificate.Subject}");
                if (loaded.Value.DisposeWithClient)
                    ownedCertificate = loaded.Value.Certificate;
            }
        }

        // Custom CA certificate for server validation
        if (!string.IsNullOrEmpty(config.SoftwareRepoCACertificate))
        {
            var validator = CreateCustomCaValidator(config.SoftwareRepoCACertificate);
            if (validator != null)
            {
                innerHandler.ServerCertificateCustomValidationCallback = validator;
                ConsoleLogger.Detail($"    Custom CA certificate loaded: {config.SoftwareRepoCACertificate}");
            }
        }

        // Dispose the inner handler first so Schannel drops the cert, then delete the temp key.
        var handler = new OwnedCertificateHandler(innerHandler, ownedCertificate);
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(60)
        };

        // Auth priority: DPAPI registry → Bearer token → Basic auth
        var authHeader = AuthService.GetAuthHeader();
        if (!string.IsNullOrEmpty(authHeader))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", authHeader);
        }
        else if (!string.IsNullOrEmpty(config.AuthToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.AuthToken);
        }
        else if (!string.IsNullOrEmpty(config.AuthUser) && !string.IsNullOrEmpty(config.AuthPassword))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{config.AuthUser}:{config.AuthPassword}"));
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
        }

        client.DefaultRequestHeaders.Add("User-Agent", "Cimian-ManagedSoftwareUpdate/1.0");

        return client;
    }

    /// <summary>
    /// Loads a client certificate from file (PEM or PFX) or Windows Certificate Store.
    /// PEM format uses separate cert + key files (Munki-compatible).
    /// PFX format uses a single file with optional password.
    /// </summary>
    private static LoadedClientCertificate? LoadClientCertificate(CimianConfig config)
    {
        // Option 1: Certificate file on disk (PEM or PFX)
        if (!string.IsNullOrEmpty(config.ClientCertificatePath))
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
                var pemCert = LoadPemCertificate(config);
                return pemCert is null
                    ? null
                    : new LoadedClientCertificate(pemCert, DisposeWithClient: true);
            }

            // PFX/P12 format — cert and key in one file. This import owns a temporary key.
            try
            {
                var pfxCert = X509CertificateLoader.LoadPkcs12FromFile(
                    config.ClientCertificatePath,
                    config.ClientCertificatePassword,
                    ClientCertificateKeyStorageFlags);
                return new LoadedClientCertificate(pfxCert, DisposeWithClient: true);
            }
            catch (Exception ex)
            {
                ConsoleLogger.Warn($"Failed to load client certificate from {config.ClientCertificatePath}: {ex.Message}");
                return null;
            }
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
                        // The store owns this key. Disposing the cert must not delete it.
                        return new LoadedClientCertificate(certs[0], DisposeWithClient: false);
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
    /// Loads a PEM certificate with a separate private key file.
    /// This is the format Munki uses: client.pem + client.key.
    /// On Windows, re-exports to PFX so the private key works with SslStream.
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
            using var cert = X509Certificate2.CreateFromPem(certPem, keyPem);

            // On Windows, re-export to PFX so the private key is usable with SslStream.
            // The PEM object is ephemeral; the imported copy owns the temporary key file.
            var exported = cert.Export(X509ContentType.Pfx);
            return X509CertificateLoader.LoadPkcs12(
                exported, null, ClientCertificateKeyStorageFlags);
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
                cert = LoadClientCertificate(config)?.Certificate;
            }

            if (cert == null)
                return null;

            var cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (string.IsNullOrEmpty(cn))
                return null;

            // Sanitize for use as URL path segment (manifest name)
            return Uri.EscapeDataString(cn);
        }
        catch
        {
            return null;
        }
        finally
        {
            cert?.Dispose();
        }
    }

    /// <summary>
    /// Disposes a file-imported client certificate after the inner HTTP handler releases it.
    /// That disposal deletes the temporary private-key file created for the handshake.
    /// </summary>
    private sealed class OwnedCertificateHandler : DelegatingHandler
    {
        private readonly X509Certificate2? _ownedCertificate;

        public OwnedCertificateHandler(HttpMessageHandler innerHandler, X509Certificate2? ownedCertificate)
            : base(innerHandler)
        {
            ArgumentNullException.ThrowIfNull(innerHandler);
            _ownedCertificate = ownedCertificate;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                _ownedCertificate?.Dispose();
        }
    }
}
