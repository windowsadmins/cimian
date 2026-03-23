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
    /// Creates an HttpClient configured with authentication and optional client certificates.
    /// Auth priority: DPAPI registry → Bearer token → Basic auth.
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

        var client = new HttpClient(handler)
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
    /// Loads a client certificate from PFX file or Windows Certificate Store.
    /// Returns null if no certificate could be loaded.
    /// </summary>
    private static X509Certificate2? LoadClientCertificate(CimianConfig config)
    {
        // Option 1: PFX file on disk
        if (!string.IsNullOrEmpty(config.ClientCertificatePath))
        {
            if (!File.Exists(config.ClientCertificatePath))
            {
                ConsoleLogger.Warn($"Client certificate file not found: {config.ClientCertificatePath}");
                return null;
            }

            try
            {
                return X509CertificateLoader.LoadPkcs12FromFile(
                    config.ClientCertificatePath,
                    config.ClientCertificatePassword,
                    X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
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
}
