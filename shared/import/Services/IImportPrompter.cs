using Cimian.CLI.Cimiimport.Models;

namespace Cimian.CLI.Cimiimport.Services;

/// <summary>
/// Abstracts the user-interaction points in <see cref="ImportService"/> so the
/// same import flow can run from the CLI (with <see cref="ConsolePrompter"/>),
/// in non-interactive scripts (<see cref="NoInteractivePrompter"/>), or from a
/// GUI host (CimianAdmin) which supplies its own implementation backed by
/// dialogs and async UI gestures.
///
/// All decision-point methods are async so a UI implementation can await user
/// input without blocking; the console implementation completes them
/// synchronously via <c>Task.FromResult</c>.
///
/// Status methods are one-way fire-and-forget — they map to console writes for
/// the CLI and to live progress / toast UI for the GUI.
/// </summary>
public interface IImportPrompter
{
    /// <summary>
    /// The pkginfo we extracted from the installer already matches an existing
    /// repo entry by Name. Show the existing item to the user and ask whether
    /// to seed the new pkginfo's catalogs/scripts/blocking_apps/etc from it.
    /// Return <c>true</c> to apply the template, <c>false</c> to start fresh.
    /// </summary>
    Task<bool> AskUseTemplateAsync(PkgsInfo existingPkg, CancellationToken cancellationToken = default);

    /// <summary>
    /// Let the user review and edit the seven metadata fields cimiimport asks
    /// about (name, version, developer, description, category, architectures,
    /// catalogs). Implementations may show a form, run console prompts, or just
    /// return <paramref name="seed"/> unchanged for non-interactive callers.
    /// </summary>
    Task<InstallerMetadata> EditMetadataAsync(
        InstallerMetadata seed,
        ImportConfiguration config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ask the user where, inside <c>pkgs/</c> and <c>pkgsinfo/</c>, the new
    /// installer + pkginfo should live. The returned string is normalised to
    /// start with <c>\</c> and have no trailing slash.
    /// </summary>
    Task<string> AskRepoSubdirAsync(string defaultPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Final review screen: present the assembled <see cref="PkgsInfo"/> to the
    /// user and confirm before any files are written. Return <c>false</c> to
    /// cancel the import.
    /// </summary>
    Task<bool> ConfirmImportAsync(PkgsInfo finalPkginfo, CancellationToken cancellationToken = default);

    /// <summary>Generic informational message (e.g. "Calculating file hash…").</summary>
    void ReportInfo(string message);

    /// <summary>Warning that doesn't abort the import (e.g. icon extraction failed).</summary>
    void ReportWarning(string message);

    /// <summary>Error message; the caller decides whether to abort.</summary>
    void ReportError(string message);
}
