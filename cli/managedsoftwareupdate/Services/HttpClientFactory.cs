using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core.Services;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Creates HttpClient instances with optional mTLS client certificate authentication.
/// Shared by ManifestService, CatalogService, and DownloadService.
/// </summary>
public static class CimianHttpClientFactory
{
    /// <summary>
    /// Creates an HttpClientHandler configured for mTLS if UseClientCertificate is enabled.
    /// </summary>
    public static HttpClientHandler CreateHandler(CimianConfig config)
    {
        var handler = new HttpClientHandler();

        if (config.UseClientCertificate)
        {
            // Load client certificate
            if (!File.Exists(config.ClientCertificatePath))
            {
                ConsoleLogger.Error($"Client certificate not found: {config.ClientCertificatePath}");
            }
            else if (!File.Exists(config.ClientKeyPath))
            {
                ConsoleLogger.Error($"Client key not found: {config.ClientKeyPath}");
            }
            else
            {
                try
                {
                    var cert = LoadCertificateWithKey(config.ClientCertificatePath, config.ClientKeyPath);
                    handler.ClientCertificates.Add(cert);
                    handler.ClientCertificateOptions = ClientCertificateOption.Manual;
                    ConsoleLogger.Info($"    mTLS: loaded client certificate CN={cert.Subject}");
                }
                catch (Exception ex)
                {
                    ConsoleLogger.Error($"Failed to load client certificate: {ex.Message}");
                }
            }

            // Load custom CA certificate for server validation
            if (!string.IsNullOrEmpty(config.CACertificatePath) && File.Exists(config.CACertificatePath))
            {
                try
                {
                    var caCert = new X509Certificate2(config.CACertificatePath);
                    handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                    {
                        if (errors == SslPolicyErrors.None)
                            return true;

                        // Build a custom chain with our CA
                        using var customChain = new X509Chain();
                        customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        customChain.ChainPolicy.CustomTrustStore.Add(caCert);

                        return cert != null && customChain.Build(cert);
                    };
                    ConsoleLogger.Info($"    mTLS: loaded CA certificate for server validation");
                }
                catch (Exception ex)
                {
                    ConsoleLogger.Error($"Failed to load CA certificate: {ex.Message}");
                }
            }
        }

        return handler;
    }

    /// <summary>
    /// Creates an HttpClient with mTLS, auth headers, and User-Agent configured.
    /// </summary>
    public static HttpClient CreateClient(CimianConfig config, TimeSpan? timeout = null)
    {
        var handler = CreateHandler(config);
        var client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(60)
        };

        // Only add token/basic auth if mTLS is not used
        if (!config.UseClientCertificate)
        {
            // First try to get auth from registry (DPAPI encrypted)
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
        }

        client.DefaultRequestHeaders.Add("User-Agent", "Cimian-ManagedSoftwareUpdate/1.0");

        return client;
    }

    /// <summary>
    /// Loads a PEM certificate and private key into an X509Certificate2.
    /// Supports PEM (.pem/.cer) and PFX (.pfx/.p12) formats.
    /// </summary>
    private static X509Certificate2 LoadCertificateWithKey(string certPath, string keyPath)
    {
        var certExt = Path.GetExtension(certPath).ToLowerInvariant();

        // PFX/P12 format — cert and key in one file
        if (certExt is ".pfx" or ".p12")
        {
            return new X509Certificate2(certPath, (string?)null, X509KeyStorageFlags.MachineKeySet);
        }

        // PEM format — separate cert and key files
        var certPem = File.ReadAllText(certPath);
        var keyPem = File.ReadAllText(keyPath);
        var cert = X509Certificate2.CreateFromPem(certPem, keyPem);

        // On Windows, we need to export/re-import to make the private key usable with SslStream
        var exported = cert.Export(X509ContentType.Pfx);
        return new X509Certificate2(exported, (string?)null, X509KeyStorageFlags.MachineKeySet);
    }

    /// <summary>
    /// Gets the CN from the client certificate, if mTLS is configured.
    /// Used for UseClientCertificateCNAsClientIdentifier.
    /// </summary>
    public static string? GetClientCertificateCN(CimianConfig config)
    {
        if (!config.UseClientCertificate || !config.UseClientCertificateCNAsClientIdentifier)
            return null;

        if (!File.Exists(config.ClientCertificatePath))
            return null;

        try
        {
            var cert = new X509Certificate2(config.ClientCertificatePath);
            var cn = cert.GetNameInfo(X509NameType.SimpleName, false);
            return string.IsNullOrEmpty(cn) ? null : cn;
        }
        catch
        {
            return null;
        }
    }
}
