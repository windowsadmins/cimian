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
