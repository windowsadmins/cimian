using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.Core.Services;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// The client certificate read from ClientCertificatePath, loaded once per process and
/// shared by every HttpClient.
///
/// Schannel cannot use an ephemeral private key for TLS client authentication
/// (https://github.com/dotnet/runtime/issues/23749), so on Windows the key is imported
/// into a machine key named <c>Cimian-ClientCertificate-&lt;pid&gt;-&lt;guid&gt;</c>. That gives
/// each run one temporary key, deleted by <see cref="Release"/> when the process exits.
/// A run that is killed cannot delete its key, so the first load in a later run deletes
/// every key with that prefix whose process is no longer running.
///
/// If the machine key cannot be created (the process is not elevated), the ephemeral
/// certificate is used as it is.
/// </summary>
internal static class SharedClientCertificate
{
    internal const string KeyNamePrefix = "Cimian-ClientCertificate-";

    private static readonly object Gate = new();
    private static (string? Path, string? KeyPath, string? Password)? _source;
    private static X509Certificate2? _certificate;
    private static CngKey? _machineKey;
    private static bool _staleKeysChecked;

    static SharedClientCertificate()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Release();
    }

    /// <summary>
    /// Returns the shared certificate for the configured file, loading it on first use.
    /// A different file (preflight can change the setting) replaces the previous one.
    /// </summary>
    public static X509Certificate2? Get(CimianConfig config)
    {
        var source = (config.ClientCertificatePath, config.ClientKeyPath, config.ClientCertificatePassword);
        lock (Gate)
        {
            if (_certificate != null && _source == source)
                return _certificate;

            ReleaseLocked();

            var fileCertificate = CimianHttpClientFactory.ReadClientCertificateFile(config);
            if (fileCertificate == null)
                return null;

            if (OperatingSystem.IsWindows())
            {
                if (!_staleKeysChecked)
                {
                    _staleKeysChecked = true;
                    RemoveStaleKeys();
                }

                try
                {
                    var (certificate, key) = MoveToMachineKey(fileCertificate);
                    fileCertificate.Dispose();
                    _certificate = certificate;
                    _machineKey = key;
                    _source = source;
                    return _certificate;
                }
                catch (Exception ex)
                {
                    ConsoleLogger.Warn($"Could not store the client certificate key as a machine key, so the TLS handshake may fail: {ex.Message}");
                }
            }

            _certificate = fileCertificate;
            _source = source;
            return _certificate;
        }
    }

    /// <summary>Disposes the shared certificate and deletes its machine key.</summary>
    public static void Release()
    {
        lock (Gate)
        {
            ReleaseLocked();
        }
    }

    private static void ReleaseLocked()
    {
        _certificate?.Dispose();
        _certificate = null;
        _source = null;

        if (_machineKey != null)
        {
            try
            {
                _machineKey.Delete();
            }
            catch (Exception ex)
            {
                ConsoleLogger.Warn($"Could not delete the client certificate machine key: {ex.Message}");
                _machineKey.Dispose();
            }
            _machineKey = null;
        }
    }

    /// <summary>
    /// Copies the certificate's private key into a new named machine key and returns the
    /// certificate bound to it.
    /// </summary>
    private static (X509Certificate2 Certificate, CngKey Key) MoveToMachineKey(X509Certificate2 fileCertificate)
    {
        using AsymmetricAlgorithm privateKey =
            (AsymmetricAlgorithm?)fileCertificate.GetRSAPrivateKey()
            ?? (AsymmetricAlgorithm?)fileCertificate.GetECDsaPrivateKey()
            ?? throw new CryptographicException("The client certificate has no RSA or ECDSA private key.");

        var pkcs8 = privateKey.ExportPkcs8PrivateKey();
        var name = $"{KeyNamePrefix}{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            NCrypt.ImportMachineKey(name, pkcs8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey);
        try
        {
            using var publicOnly = X509CertificateLoader.LoadCertificate(fileCertificate.RawData);
            X509Certificate2 certificate = privateKey is RSA
                ? publicOnly.CopyWithPrivateKey(new RSACng(key))
                : publicOnly.CopyWithPrivateKey(new ECDsaCng(key));
            return (certificate, key);
        }
        catch
        {
            key.Delete();
            throw;
        }
    }

    /// <summary>
    /// Deletes the machine keys an earlier run left behind: keys with our prefix whose
    /// process is not a running copy of this program.
    /// </summary>
    private static void RemoveStaleKeys()
    {
        try
        {
            var processName = Process.GetCurrentProcess().ProcessName;
            foreach (var name in NCrypt.EnumerateMachineKeyNames())
            {
                if (!IsStaleKeyName(name, processName, IsRunning))
                    continue;

                try
                {
                    using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey);
                    key.Delete();
                    ConsoleLogger.Detail($"    Deleted a client certificate key left by an earlier run: {name}");
                }
                catch (Exception ex)
                {
                    ConsoleLogger.Detail($"    Could not delete stale client certificate key {name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Detail($"    Could not check for stale client certificate keys: {ex.Message}");
        }
    }

    private static bool IsRunning(int processId, string processName)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// True for a key named by this class whose process is not a running copy of
    /// <paramref name="processName"/>. The current process's own keys are never stale.
    /// </summary>
    internal static bool IsStaleKeyName(string keyName, string processName, Func<int, string, bool> isRunning)
    {
        if (!keyName.StartsWith(KeyNamePrefix, StringComparison.Ordinal))
            return false;

        var rest = keyName[KeyNamePrefix.Length..];
        var dash = rest.IndexOf('-');
        if (dash <= 0 || !int.TryParse(rest[..dash], out var processId))
            return false;

        if (processId == Environment.ProcessId)
            return false;

        return !isRunning(processId, processName);
    }

    /// <summary>The NCrypt calls CngKey does not expose: a named PKCS#8 import and key enumeration.</summary>
    private static class NCrypt
    {
        private const int NCRYPTBUFFER_PKCS_KEY_NAME = 45;
        private const int NCRYPT_MACHINE_KEY_FLAG = 0x20;
        private const int NCRYPT_DO_NOT_FINALIZE_FLAG = 0x400;
        private const int NTE_NO_MORE_ITEMS = unchecked((int)0x8009002A);

        [StructLayout(LayoutKind.Sequential)]
        private struct NCryptBuffer
        {
            public int cbBuffer;
            public int BufferType;
            public IntPtr pvBuffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NCryptBufferDesc
        {
            public int ulVersion;
            public int cBuffers;
            public IntPtr pBuffers;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NCryptKeyName
        {
            public IntPtr pszName;
            public IntPtr pszAlgid;
            public int dwLegacyKeySpec;
            public int dwFlags;
        }

        [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int NCryptOpenStorageProvider(out IntPtr phProvider, string pszProviderName, int dwFlags);

        [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int NCryptImportKey(IntPtr hProvider, IntPtr hImportKey, string pszBlobType,
            ref NCryptBufferDesc pParameterList, out IntPtr phKey, byte[] pbData, int cbData, int dwFlags);

        [DllImport("ncrypt.dll")]
        private static extern int NCryptFinalizeKey(IntPtr hKey, int dwFlags);

        [DllImport("ncrypt.dll")]
        private static extern int NCryptEnumKeys(IntPtr hProvider, IntPtr pszScope, out IntPtr ppKeyName,
            ref IntPtr ppEnumState, int dwFlags);

        [DllImport("ncrypt.dll")]
        private static extern int NCryptFreeBuffer(IntPtr pvInput);

        [DllImport("ncrypt.dll")]
        private static extern int NCryptFreeObject(IntPtr hObject);

        private static IntPtr OpenProvider()
        {
            Check(NCryptOpenStorageProvider(out var provider, CngProvider.MicrosoftSoftwareKeyStorageProvider.Provider, 0));
            return provider;
        }

        public static void ImportMachineKey(string name, byte[] pkcs8)
        {
            var provider = OpenProvider();
            var namePtr = Marshal.StringToHGlobalUni(name);
            var bufferPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NCryptBuffer>());
            var key = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(new NCryptBuffer
                {
                    cbBuffer = (name.Length + 1) * 2,
                    BufferType = NCRYPTBUFFER_PKCS_KEY_NAME,
                    pvBuffer = namePtr
                }, bufferPtr, false);
                var parameters = new NCryptBufferDesc { ulVersion = 0, cBuffers = 1, pBuffers = bufferPtr };

                Check(NCryptImportKey(provider, IntPtr.Zero, "PKCS8_PRIVATEKEY", ref parameters, out key,
                    pkcs8, pkcs8.Length, NCRYPT_MACHINE_KEY_FLAG | NCRYPT_DO_NOT_FINALIZE_FLAG));
                Check(NCryptFinalizeKey(key, 0));
            }
            finally
            {
                if (key != IntPtr.Zero) NCryptFreeObject(key);
                Marshal.FreeHGlobal(bufferPtr);
                Marshal.FreeHGlobal(namePtr);
                NCryptFreeObject(provider);
            }
        }

        public static List<string> EnumerateMachineKeyNames()
        {
            var names = new List<string>();
            var provider = OpenProvider();
            var enumState = IntPtr.Zero;
            try
            {
                while (true)
                {
                    var status = NCryptEnumKeys(provider, IntPtr.Zero, out var keyName, ref enumState, NCRYPT_MACHINE_KEY_FLAG);
                    if (status == NTE_NO_MORE_ITEMS)
                        break;
                    Check(status);
                    try
                    {
                        var entry = Marshal.PtrToStructure<NCryptKeyName>(keyName);
                        var name = Marshal.PtrToStringUni(entry.pszName);
                        if (name != null)
                            names.Add(name);
                    }
                    finally
                    {
                        NCryptFreeBuffer(keyName);
                    }
                }
            }
            finally
            {
                if (enumState != IntPtr.Zero) NCryptFreeBuffer(enumState);
                NCryptFreeObject(provider);
            }
            return names;
        }

        private static void Check(int status)
        {
            if (status != 0)
                throw new CryptographicException(status);
        }
    }
}
