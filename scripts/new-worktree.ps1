#Requires -Version 7.0
<#
.SYNOPSIS
    Create a CimianTools worktree with all nested submodules initialised.

.DESCRIPTION
    Wraps `git worktree add --recurse-submodules` so the cli/cimipkg
    submodule is checked out in the new worktree, matching what
    build.ps1 expects. Plain `git worktree add` does not recurse into
    submodules even with submodule.recurse=true configured, so the
    build fails until contributors run `git submodule update --init`
    manually inside the new worktree.

    All arguments are forwarded verbatim to `git worktree add`, so any
    flag accepted by git (e.g. -b, --detach, --force) works here too.

.EXAMPLE
    .\scripts\new-worktree.ps1 .claude/worktrees/my-feature -b feature/my-feature

.EXAMPLE
    .\scripts\new-worktree.ps1 ../Cimian.worktrees/hotfix main
#>
param(
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
    [string[]]$WorktreeArgs
)

& git worktree add --recurse-submodules @WorktreeArgs
exit $LASTEXITCODE
