using Cimian.CLI.Cimiimport.Models;

namespace Cimian.CLI.Cimiimport.Services;

/// <summary>
/// <see cref="IImportPrompter"/> used when cimiimport runs with
/// <c>--nointeractive</c>: every decision accepts the default, but status
/// messages still go to the console so the operator can read the log
/// afterwards. Matches the prior <c>noInteractive=true</c> code path in
/// <see cref="ImportService"/>.
/// </summary>
public sealed class NoInteractivePrompter : IImportPrompter
{
    /// <summary>Auto-apply template when one exists (prior CLI behaviour).</summary>
    public Task<bool> AskUseTemplateAsync(PkgsInfo existingPkg, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    /// <summary>Return the seed metadata unchanged — caller supplied via CLI flags.</summary>
    public Task<InstallerMetadata> EditMetadataAsync(
        InstallerMetadata seed,
        ImportConfiguration config,
        CancellationToken cancellationToken = default)
        => Task.FromResult(seed);

    /// <summary>Take the default repo subdirectory verbatim, normalised.</summary>
    public Task<string> AskRepoSubdirAsync(string defaultPath, CancellationToken cancellationToken = default)
    {
        var path = defaultPath;
        if (!path.StartsWith('\\'))
        {
            path = "\\" + path;
        }
        return Task.FromResult(path.TrimEnd('\\'));
    }

    /// <summary>Non-interactive mode always proceeds with the import.</summary>
    public Task<bool> ConfirmImportAsync(PkgsInfo finalPkginfo, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public void ReportInfo(string message) => Console.WriteLine(message);

    public void ReportWarning(string message) => Console.WriteLine($"[WARN] {message}");

    public void ReportError(string message) => Console.WriteLine($"[ERROR] {message}");
}
