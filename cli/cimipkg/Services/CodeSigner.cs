using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// Signs a single PowerShell script using signtool.exe directly.
    /// Uses signtool instead of PowerShell's Set-AuthenticodeSignature to avoid
    /// Cert: drive availability issues when launched as a subprocess from .NET.
    /// </summary>
    /// <param name="scriptPath">Path to the script to sign.</param>
    /// <param name="certSubject">Certificate subject name.</param>
    /// <param name="certThumbprint">Certificate thumbprint (optional, takes precedence).</param>
    public void SignPowerShellScript(string scriptPath, string? certSubject, string? certThumbprint)
    {
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("Script file not found.", scriptPath);
        }

        var signtoolPath = FindSignTool();

        // Build signtool arguments matching the Go implementation
        var args = new StringBuilder("sign ");

        if (!string.IsNullOrEmpty(certThumbprint))
        {
            args.Append($"/sha1 {certThumbprint} ");
            _logger.LogDebug("Signing {Script} with thumbprint: {Thumbprint}", Path.GetFileName(scriptPath), certThumbprint);
        }
        else if (!string.IsNullOrEmpty(certSubject))
        {
            args.Append($"/n \"{certSubject}\" ");
            _logger.LogDebug("Signing {Script} with certificate: {Subject}", Path.GetFileName(scriptPath), certSubject);
        }
        else
        {
            throw new InvalidOperationException($"No certificate specified for {scriptPath}");
        }

        args.Append("/fd SHA256 ");
        args.Append("/tr http://timestamp.digicert.com ");
        args.Append("/td SHA256 ");
        args.Append($"\"{scriptPath}\"");

        var psi = new ProcessStartInfo
        {
            FileName = signtoolPath,
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start signtool.exe process");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to sign script {scriptPath}: {error}{output}");
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
    /// Finds signtool.exe in Windows SDK paths.
    /// Searches common SDK installation directories for the signing tool.
    /// </summary>
    private static string FindSignTool()
    {
        // Check if signtool is on PATH first
        var pathResult = FindOnPath("signtool.exe");
        if (pathResult != null)
            return pathResult;

        // Search Windows SDK directories
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var sdkRoot = Path.Combine(programFilesX86, "Windows Kits", "10", "bin");

        if (Directory.Exists(sdkRoot))
        {
            // Get version directories, sorted descending to prefer newest SDK
            var versionDirs = Directory.GetDirectories(sdkRoot, "10.*")
                .OrderByDescending(d => d)
                .ToArray();

            foreach (var versionDir in versionDirs)
            {
                // Try x64 first, then x86
                foreach (var arch in new[] { "x64", "x86" })
                {
                    var signtoolPath = Path.Combine(versionDir, arch, "signtool.exe");
                    if (File.Exists(signtoolPath))
                        return signtoolPath;
                }
            }
        }

        throw new FileNotFoundException(
            "signtool.exe not found. Install a Windows 10/11 SDK or ensure signtool.exe is on PATH.");
    }

    /// <summary>
    /// Finds an executable on the system PATH.
    /// </summary>
    private static string? FindOnPath(string executable)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
            return null;

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            var fullPath = Path.Combine(dir, executable);
            if (File.Exists(fullPath))
                return fullPath;
        }

        return null;
    }
}
