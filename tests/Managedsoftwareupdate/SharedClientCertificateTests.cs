using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests for SharedClientCertificate: a ClientCertificatePath certificate is loaded once
/// per process and shared, and only keys left by a run that is no longer running are stale.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public class SharedClientCertificateTests : IDisposable
{
    private readonly string _testDir = Path.Combine(Path.GetTempPath(), "CimianTests", Guid.NewGuid().ToString());

    public SharedClientCertificateTests()
    {
        Directory.CreateDirectory(_testDir);
        SharedClientCertificate.Release();
    }

    public void Dispose()
    {
        SharedClientCertificate.Release();
        try
        {
            Directory.Delete(_testDir, recursive: true);
        }
        catch { /* Ignore cleanup errors */ }
    }

    private static X509Certificate2 NewCertificate(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private CimianConfig PfxConfig(string subject)
    {
        using var certificate = NewCertificate(subject);
        var path = Path.Combine(_testDir, subject + ".pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, "secret"));
        return new CimianConfig { ClientCertificatePath = path, ClientCertificatePassword = "secret" };
    }

    [Fact]
    public void Get_SameFile_ReturnsTheSameCertificate()
    {
        var config = PfxConfig("same");

        var first = SharedClientCertificate.Get(config);
        var second = SharedClientCertificate.Get(config);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.True(first!.HasPrivateKey);
    }

    [Fact]
    public void Get_DifferentFile_ReplacesAndDisposesThePreviousCertificate()
    {
        var first = SharedClientCertificate.Get(PfxConfig("first"));
        var second = SharedClientCertificate.Get(PfxConfig("second"));

        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal("CN=second", second!.Subject);
        Assert.Equal(IntPtr.Zero, first!.Handle);
    }

    [Fact]
    public void Get_AfterRelease_LoadsTheFileAgain()
    {
        var config = PfxConfig("released");
        var first = SharedClientCertificate.Get(config);

        SharedClientCertificate.Release();
        var second = SharedClientCertificate.Get(config);

        Assert.NotSame(first, second);
        Assert.True(second!.HasPrivateKey);
    }

    [Fact]
    public void Get_PemWithKeyFile_HasAPrivateKey()
    {
        using var certificate = NewCertificate("pem");
        using var key = certificate.GetRSAPrivateKey()!;
        var certPath = Path.Combine(_testDir, "client.pem");
        var keyPath = Path.Combine(_testDir, "client.key");
        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());

        var loaded = SharedClientCertificate.Get(new CimianConfig { ClientCertificatePath = certPath, ClientKeyPath = keyPath });

        Assert.NotNull(loaded);
        Assert.True(loaded!.HasPrivateKey);
        Assert.Equal("CN=pem", loaded.Subject);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_FileCertificate_CompletesMutualTlsAndDeletesMachineKey(bool pem)
    {
        if (!OperatingSystem.IsWindows()) return;

        using var original = NewCertificate("mtls-client");
        var certPath = Path.Combine(_testDir, pem ? "mtls.pem" : "mtls.pfx");
        var keyPath = Path.Combine(_testDir, "mtls.key");
        var config = new CimianConfig { ClientCertificatePath = certPath };
        if (pem)
        {
            using var originalKey = original.GetRSAPrivateKey()!;
            File.WriteAllText(certPath, original.ExportCertificatePem());
            File.WriteAllText(keyPath, originalKey.ExportPkcs8PrivateKeyPem());
            config.ClientKeyPath = keyPath;
        }
        else
        {
            File.WriteAllBytes(certPath, original.Export(X509ContentType.Pfx));
        }

        var certificate = SharedClientCertificate.Get(config);
        Assert.NotNull(certificate);
        using var rsa = certificate!.GetRSAPrivateKey();
        var cng = Assert.IsType<RSACng>(rsa);
        Assert.True(cng.Key.IsMachineKey);
        var keyName = cng.Key.KeyName!;
        Assert.False(cng.Key.IsEphemeral);

        using var serverCertificate = NewCertificate("localhost");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(token);
            using var tls = new SslStream(connection.GetStream(), false,
                (_, remote, _, _) => remote?.GetCertHashString() == original.Thumbprint);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCertificate,
                ClientCertificateRequired = true,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, token);
            Assert.NotNull(tls.RemoteCertificate);
            Assert.Equal(original.Thumbprint, tls.RemoteCertificate!.GetCertHashString());
            await tls.WriteAsync(new byte[] { 42 }, token);
        }, token);

        using var connection = new TcpClient();
        await connection.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, token);
        using (var tls = new SslStream(connection.GetStream(), false, (_, _, _, _) => true))
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ClientCertificates = new X509CertificateCollection { certificate },
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, token);
            var response = new byte[1];
            Assert.Equal(1, await tls.ReadAsync(response, token));
            Assert.Equal(42, response[0]);
            await server;
        }

        rsa!.Dispose();
        SharedClientCertificate.Release();
        Assert.False(CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider,
            CngKeyOpenOptions.MachineKey));
    }

    [Fact]
    public void Get_MissingFile_ReturnsNull()
    {
        var config = new CimianConfig { ClientCertificatePath = Path.Combine(_testDir, "missing.pfx") };

        Assert.Null(SharedClientCertificate.Get(config));
    }

    [Theory]
    [InlineData("Cimian-ClientCertificate-4242-0123abcd", false, true)]
    [InlineData("Cimian-ClientCertificate-4242-0123abcd", true, false)]
    [InlineData("SomeOtherApp-4242-0123abcd", false, false)]
    [InlineData("Cimian-ClientCertificate-notapid-0123abcd", false, false)]
    [InlineData("Cimian-ClientCertificate-4242", false, false)]
    public void IsStaleKeyName_OnlyOurKeysFromAProcessNoLongerRunning(string keyName, bool running, bool expected)
    {
        Assert.Equal(expected, SharedClientCertificate.IsStaleKeyName(keyName, "managedsoftwareupdate", (_, _) => running));
    }

    [Fact]
    public void IsStaleKeyName_CurrentProcessKey_IsNeverStale()
    {
        var keyName = $"{SharedClientCertificate.KeyNamePrefix}{Environment.ProcessId}-0123abcd";

        Assert.False(SharedClientCertificate.IsStaleKeyName(keyName, "managedsoftwareupdate", (_, _) => false));
    }
}
