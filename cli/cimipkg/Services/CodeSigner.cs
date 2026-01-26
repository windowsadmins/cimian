using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Cimian.CLI.Cimipkg.Models;
using Microsoft.Extensions.Logging;

namespace Cimian.CLI.Cimipkg.Services;

/// <summary>
/// Handles code signing for PowerShell scripts and NuGet packages.
/// </summary>
public class CodeSigner
{
    private readonly ILogger<CodeSigner> _logger;

    public CodeSigner(ILogger<CodeSigner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Signs all PowerShell scripts in a directory using Authenticode.
    /// </summary>
    /// <param name="directory">Directory containing scripts to sign.</param>
    /// <param name="certSubject">Certificate subject name (CN=...).</param>
    /// <param name="certThumbprint">Certificate thumbprint (optional, takes precedence).</param>
    public void SignPowerShellScriptsInDirectory(string directory, string? certSubject, string? certThumbprint)
    {
        if (string.IsNullOrEmpty(certSubject) && string.IsNullOrEmpty(certThumbprint))
        {
            _logger.LogDebug("No signing certificate specified, skipping script signing");
            return;
        }

        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("Directory does not exist: {Directory}", directory);
            return;
        }

        var ps1Files = Directory.GetFiles(directory, "*.ps1", SearchOption.AllDirectories);
        foreach (var scriptPath in ps1Files)
        {
            SignPowerShellScript(scriptPath, certSubject, certThumbprint);
        }

        _logger.LogInformation("Signed {Count} PowerShell script(s) in {Directory}", ps1Files.Length, directory);
    }

    /// <summary>
    /// Signs a single PowerShell script using Authenticode.
    /// </summary>
    /// <param name="scriptPath">Path to the script to sign.</param>
    /// <param name="certSubject">Certificate subject name.</param>
    /// <param name="certThumbprint">Certificate thumbprint (optional).</param>
    public void SignPowerShellScript(string scriptPath, string? certSubject, string? certThumbprint)
    {
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("Script file not found.", scriptPath);
        }

        // Build PowerShell command to sign the script
        // Note: Accept certificates with code signing EKU OR no EKU (universal certificates)
        // A certificate with no EnhancedKeyUsageList can be used for any purpose
        var getCertCommand = !string.IsNullOrEmpty(certThumbprint)
            ? $"Get-ChildItem -Path Cert:\\CurrentUser\\My | Where-Object {{ $_.Thumbprint -eq '{certThumbprint}' -and ($_.EnhancedKeyUsageList.Count -eq 0 -or $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3') }}"
            : $"Get-ChildItem -Path Cert:\\CurrentUser\\My | Where-Object {{ $_.Subject -like '*{certSubject}*' -and ($_.EnhancedKeyUsageList.Count -eq 0 -or $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3') }}";

        var psCommand = @"
$cert = " + getCertCommand + @" | Select-Object -First 1
if (-not $cert) {
    $cert = Get-ChildItem -Path Cert:\LocalMachine\My | Where-Object { $_.Subject -like '*" + certSubject + @"*' -and ($_.EnhancedKeyUsageList.Count -eq 0 -or $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3') } | Select-Object -First 1
}
if (-not $cert) {
    throw 'Code signing certificate not found'
}
Set-AuthenticodeSignature -FilePath '" + scriptPath + @"' -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
";

        var result = RunPowerShellCommand(psCommand);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to sign script {scriptPath}: {result.Error}");
        }

        _logger.LogDebug("Signed script: {ScriptPath}", scriptPath);
    }

    /// <summary>
    /// Signs a NuGet package (.nupkg or .pkg) using nuget sign.
    /// </summary>
    /// <param name="packagePath">Path to the package to sign.</param>
    /// <param name="certSubject">Certificate subject name.</param>
    public void SignNuGetPackage(string packagePath, string certSubject)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("Package file not found.", packagePath);
        }

        // Try using nuget sign command
        var psi = new ProcessStartInfo
        {
            FileName = "nuget",
            Arguments = $"sign \"{packagePath}\" -CertificateSubjectName \"{certSubject}\" -Timestamper http://timestamp.digicert.com",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                _logger.LogWarning("Failed to start nuget sign process. Package will not be signed.");
                return;
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd();
                _logger.LogWarning("nuget sign failed: {Error}", error);
                _logger.LogWarning("Package {PackagePath} was not signed", packagePath);
            }
            else
            {
                _logger.LogInformation("Package signed: {PackagePath}", packagePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sign package (nuget may not be available)");
        }
    }

    /// <summary>
    /// Gets certificate information from the certificate store.
    /// </summary>
    /// <param name="certSubject">Certificate subject name to search for.</param>
    /// <param name="certThumbprint">Certificate thumbprint to search for (takes precedence).</param>
    /// <returns>Certificate information if found.</returns>
    public CertificateInfo? GetCertificateInfo(string? certSubject, string? certThumbprint)
    {
        if (string.IsNullOrEmpty(certSubject) && string.IsNullOrEmpty(certThumbprint))
        {
            return null;
        }

        // Try CurrentUser store first, then LocalMachine
        var storeLocations = new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine };

        foreach (var storeLocation in storeLocations)
        {
            using var store = new X509Store(StoreName.My, storeLocation);
            try
            {
                store.Open(OpenFlags.ReadOnly);

                foreach (var cert in store.Certificates)
                {
                    // Match by thumbprint if provided
                    if (!string.IsNullOrEmpty(certThumbprint))
                    {
                        if (string.Equals(cert.Thumbprint, certThumbprint, StringComparison.OrdinalIgnoreCase))
                        {
                            return CreateCertificateInfo(cert);
                        }
                    }
                    // Match by subject
                    else if (!string.IsNullOrEmpty(certSubject))
                    {
                        if (cert.Subject.Contains(certSubject, StringComparison.OrdinalIgnoreCase))
                        {
                            return CreateCertificateInfo(cert);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to access certificate store: {StoreLocation}", storeLocation);
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a package signature for embedding in build-info.yaml.
    /// </summary>
    /// <param name="packageDir">Directory containing package contents.</param>
    /// <param name="certSubject">Certificate subject name.</param>
    /// <param name="certThumbprint">Certificate thumbprint.</param>
    /// <returns>Package signature if certificate is found.</returns>
    public PackageSignature? CreatePackageSignature(string packageDir, string? certSubject, string? certThumbprint)
    {
        var certInfo = GetCertificateInfo(certSubject, certThumbprint);
        if (certInfo == null)
        {
            _logger.LogWarning("Could not find signing certificate");
            return null;
        }

        // Calculate content hash of all files in the package directory
        var contentHash = CalculateDirectoryHash(packageDir);

        // Create signed hash (content hash + thumbprint)
        using var sha256 = SHA256.Create();
        var signedHashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(contentHash + certInfo.Thumbprint));
        var signedHash = Convert.ToBase64String(signedHashBytes);

        return new PackageSignature
        {
            Algorithm = "SHA256",
            Certificate = certInfo,
            PackageHash = contentHash,
            ContentHash = contentHash,
            SignedHash = signedHash,
            Timestamp = DateTime.UtcNow.ToString("O"),
            Version = "1.0"
        };
    }

    /// <summary>
    /// Calculates a combined hash of all files in a directory.
    /// </summary>
    private string CalculateDirectoryHash(string directory)
    {
        var sb = new StringBuilder();
        using var sha256 = SHA256.Create();

        foreach (var filePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            // Skip build-info.yaml as it will be modified with the signature
            if (Path.GetFileName(filePath).Equals("build-info.yaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(directory, filePath);
            var fileBytes = File.ReadAllBytes(filePath);
            var fileHash = sha256.ComputeHash(fileBytes);
            sb.Append(relativePath);
            sb.Append(':');
            sb.Append(Convert.ToHexString(fileHash).ToLowerInvariant());
            sb.Append('|');
        }

        // Hash the combined file hashes
        var combinedHash = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(combinedHash).ToLowerInvariant();
    }

    /// <summary>
    /// Creates CertificateInfo from an X509Certificate2.
    /// </summary>
    private static CertificateInfo CreateCertificateInfo(X509Certificate2 cert)
    {
        return new CertificateInfo
        {
            Subject = cert.Subject,
            Issuer = cert.Issuer,
            Thumbprint = cert.Thumbprint,
            SerialNumber = cert.SerialNumber,
            NotBefore = cert.NotBefore.ToString("O"),
            NotAfter = cert.NotAfter.ToString("O")
        };
    }

    /// <summary>
    /// Runs a PowerShell command and returns the result.
    /// Uses a temp script file instead of inline command to ensure proper provider loading.
    /// Uses pwsh.exe (PowerShell 7+) which includes the Certificate provider by default.
    /// </summary>
    private (bool Success, string Output, string Error) RunPowerShellCommand(string command)
    {
        // Write command to temp file to avoid complex escaping and ensure proper provider loading
        var tempScript = Path.Combine(Path.GetTempPath(), $"cimipkg_sign_{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(tempScript, command, Encoding.UTF8);

            // Use pwsh.exe (PowerShell 7+) which has the Certificate provider built-in
            // Fall back to powershell.exe if pwsh is not available
            var powershellPath = FindPowerShellExecutable();
            
            var psi = new ProcessStartInfo
            {
                FileName = powershellPath,
                Arguments = $"-ExecutionPolicy Bypass -File \"{tempScript}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return (false, "", "Failed to start PowerShell process");
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode == 0, output, error);
        }
        finally
        {
            // Cleanup temp script
            try { File.Delete(tempScript); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Finds the appropriate PowerShell executable.
    /// Prefers pwsh.exe (PowerShell 7+) which has built-in Certificate provider.
    /// </summary>
    private static string FindPowerShellExecutable()
    {
        // Check for PowerShell 7+ (pwsh.exe) first
        var pwshPath = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\PowerShell\7\pwsh.exe");
        if (File.Exists(pwshPath))
        {
            return pwshPath;
        }

        // Try finding pwsh in PATH
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>();
        foreach (var dir in pathDirs)
        {
            var pwshInPath = Path.Combine(dir, "pwsh.exe");
            if (File.Exists(pwshInPath))
            {
                return pwshInPath;
            }
        }

        // Fallback to Windows PowerShell
        return "powershell.exe";
    }
}
