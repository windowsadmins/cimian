using System.Diagnostics;

namespace Cimian.CLI.Cimiimport.Services;

/// <summary>
/// Resolves the Cimian deployment repo path at runtime instead of relying on a
/// machine-specific default baked into the binary.
///
/// Resolution order (mirrors .githooks/sync-lib.ps1 → Resolve-CimianRepo):
///   1. Walk up from cwd looking for any ancestor containing deployment/pkgsinfo/.
///      That's the marker of a real Cimian deployment workspace — present in the
///      outer repo even when running from a submodule under packages/.
///   2. If that ancestor is also a git checkout whose origin matches the Cimian
///      remote pattern, accept it.
///   3. Otherwise return null — caller must prompt the user explicitly rather
///      than silently fall back to a stale guess.
/// </summary>
public static class RepoResolver
{
    private const string CimianRemotePattern = "emilycarru-its-infra/Devices/_git/Cimian";

    public static string? ResolveDefaultRepoPath()
    {
        var deploymentRoot = FindAncestorWithDeployment(Directory.GetCurrentDirectory());
        if (deploymentRoot is null) return null;
        if (!RemoteMatchesCimian(deploymentRoot)) return null;
        return Path.Combine(deploymentRoot, "deployment");
    }

    private static string? FindAncestorWithDeployment(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "deployment", "pkgsinfo")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static bool RemoteMatchesCimian(string repoRoot)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "remote get-url origin")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return p.ExitCode == 0 &&
                   stdout.Contains(CimianRemotePattern, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
