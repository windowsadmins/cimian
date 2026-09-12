# managedsoftwareupdate

`managedsoftwareupdate` is the Cimian client engine. It fetches the machine's
manifests and catalogs, decides which items need installing, updating or
removing, performs those actions, and writes the logs and reports that everything
else reads. This page is the complete command reference; the decision logic
itself is described in [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed).

It requires administrator rights for a real run and is normally invoked by the
hourly scheduled task or by [cimiwatcher](cimiwatcher) rather than by hand.

## Synopsis

```
managedsoftwareupdate [-a|--auto] [-c|--checkonly] [-i|--installonly] [-b|--bootstrap]
                      [-m|--manifest <name>] [--local-only-manifest <path>] [--item <name>...]
                      [--config <path>] [--show-config] [--show-status] [--status-port <n>]
                      [--no-preflight] [--no-postflight] [--preflight-only] [--postflight-only]
                      [--set-bootstrap-mode] [--clear-bootstrap-mode]
                      [--cache-status] [--validate-cache] [--clean-cache]
                      [--clear-loop <name|all>] [--loop-status]
                      [--selfupdate-status] [--check-selfupdate] [--perform-selfupdate]
                      [--clear-selfupdate] [--restart-service] [--self-check]
                      [-v|-vv|-vvv] [-q|--quiet] [-V|--version]
```

There are no subcommands. Everything is a flag.

## Run modes

The mode flags are not mutually exclusive at the parser level, but the engine
picks exactly one run type per session, in this precedence order: bootstrap,
auto, checkonly, installonly, manual. The run type is recorded in the session log
and in the reports, so it is worth knowing which one you actually got.

**manual** is the default when no mode flag is given. The full session runs:
check, download, install, uninstall. Restart and logout actions are only
*recommended*, not performed.

**auto** (`-a`) is the mode the hourly scheduled task uses. It behaves like a
manual run with two differences: items are deferred when a user is actively
logged in and the item would disrupt them, and a restart or logout requested by
an installed item is actually carried out — a restart runs `shutdown /r /t 300`,
giving the user a five-minute grace period.

**checkonly** (`-c`) stops after the decision phase. It prints the pending
tables, writes `InstallInfo.yaml`, ends the session and returns 0. Nothing is
downloaded or installed, and the postflight script does **not** run.

**installonly** (`-i`) installs what is already pending without treating the run
as an ordinary check cycle.

**bootstrap** (`-b`) is first-run provisioning mode, also entered automatically
whenever `C:\ProgramData\ManagedInstalls\.cimian.bootstrap` exists. In bootstrap
mode [Install Loop Prevention](Install-Loop-Prevention) is fully disabled — a
freshly imaged machine must be allowed to install everything, however many times
it takes — and restart and logout actions are performed as in auto mode. There is
no convergence loop inside the process: each invocation is one session, and
repetition comes from the hourly task. The bootstrap flag clears itself only when
every install and uninstall in a session succeeded.

## Flags

| Flag | Short | Argument | Default | Effect |
|---|---|---|---|---|
| `--auto` | `-a` | — | off | Run in auto mode (see above). |
| `--checkonly` | `-c` | — | off | Check only; do not download or install. |
| `--installonly` | `-i` | — | off | Install pending updates without re-checking. |
| `--bootstrap` | `-b` | — | off | Run this session in bootstrap mode without writing the flag file. |
| `--set-bootstrap-mode` | — | — | off | Write the bootstrap flag file so the next run is a bootstrap run, then exit. |
| `--clear-bootstrap-mode` | — | — | off | Delete the bootstrap flag file, then exit. |
| `--manifest` | `-m` | manifest name | none | Process only the named server manifest. |
| `--local-only-manifest` | — | file path | none | Use a local manifest file instead of the server manifest. |
| `--item` | — | one or more item names | none | Process only the named items. Pass every name after a single flag; the flag is not repeatable. |
| `--config` | — | file path | `C:\ProgramData\ManagedInstalls\Config.yaml` | Read configuration from a different file. |
| `--show-config` | — | — | off | Print the effective configuration and exit. |
| `--show-status` | — | — | off | Report progress to a GUI status listener over loopback TCP. |
| `--status-port` | — | port number | `19847` | Port of that listener. 19847 is [cimistatus](cimistatus); [Managed Software Center](Managed-Software-Center) passes 19848 for itself. |
| `--no-preflight` | — | — | off | Skip the preflight script. |
| `--no-postflight` | — | — | off | Skip the postflight script. |
| `--preflight-only` | — | — | off | Run only the preflight script, then exit. |
| `--postflight-only` | — | — | off | Run only the postflight script, then exit. |
| `--cache-status` | — | — | off | Print cache path, file count, total size, oldest file, corrupt-file count, and the `UseCache` and `CacheRetentionDays` settings, then exit. |
| `--validate-cache` | — | — | off | Validate cache integrity, delete corrupt files, then exit. |
| `--clean-cache` | — | — | off | Delete everything under the cache directory and prune empty directories, then exit. |
| `--clear-loop` | — | item name or `all` | none | Clear loop suppression for one item, or for every item, then exit. |
| `--loop-status` | — | — | off | Print loop-guard diagnostics for each suppressed item, then exit. |
| `--selfupdate-status` | — | — | off | Show self-update status and exit. |
| `--check-selfupdate` | — | — | off | Report whether a self-update is staged, then exit. |
| `--perform-selfupdate` | — | — | off | Apply the staged self-update. Intended for internal use by the watcher. |
| `--clear-selfupdate` | — | — | off | Clear the pending self-update flag, then exit. |
| `--restart-service` | — | — | off | Stop and start the `CimianWatcher` service (30-second waits, 2-second gap), then exit. |
| `--self-check` | — | — | off | Health check for the Watchdog scheduled task. See the exit codes below. |
| `--quiet` | `-q` | — | off | **Parsed and ignored.** The value is never read; output is unchanged. |
| `--version` | `-V` | — | off | Print the version and exit — but only when it is the sole argument. Combined with any other flag it is ignored and a normal run proceeds. |

`--item` takes several names after one flag. Repeating the flag fails with `Option 'item' is defined multiple times` and exits 1:

```
managedsoftwareupdate --auto --item "Example App" "Another App"
```

### Verbosity

Verbosity is handled before parsing rather than by the parser. Any argument that
is a single dash followed only by `v` characters increments a counter, as does
the literal `--verbose`. `-vvv` and `-v -v -v` are therefore equivalent.

| Level | Reached by | Effect |
|---|---|---|
| 0 | no verbosity argument | Default log level. |
| 1 | `-v` or `--verbose` | Verbose output, log level INFO. |
| 2 | `-vv` | No distinct configuration change from level 1, but the engine prints stack traces on errors at this level and above. |
| 3 or more | `-vvv` | Debug output, log level DEBUG. |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | The session completed, or an informational flag did its job. |
| 1 | Argument parse error; another instance is already running; not running as administrator; one or more operations failed; an unhandled exception; a `--preflight-only`, `--postflight-only`, self-update or `--restart-service` failure. |
| 2 | `--self-check` only: drift detected. One or more required binaries are missing and the drift marker was written. |
| 3 | `--self-check` only: the drift marker could not be written, or an unexpected error occurred. |

`--self-check` verifies that `managedsoftwareupdate.exe`, `cimitrigger.exe` and
`cimiwatcher.exe` are present in `C:\Program Files\Cimian`, and writes a
single-line JSON result to
`C:\ProgramData\ManagedInstalls\cimian_selfcheck.json`. It is what the "Cimian
Watchdog" scheduled task runs, so exit code 2 means "the installation is
incomplete", not "the check failed to run" — that is code 3.

## Single instance

Only one `managedsoftwareupdate` may run at a time; the lock is a global mutex.
If a second copy starts and `--checkonly` was not given, it prints
`Another instance of managedsoftwareupdate is running. Exiting.` to standard
error and returns 1.

With `--checkonly` it instead prompts on standard error, reporting what the
running process appears to be doing and offering `[K]` to kill the process tree
and retry, `[W]` to wait (polling every two seconds), or `[Q]` to quit. Quit is
the default and is also what any unrecognised answer does. Do not use
`--checkonly` in an unattended script without redirecting or supplying input.

## What a run does, in order

1. Start the session log and reap any orphaned sessions left by a killed run.
2. Verify administrator rights; abort with exit 1 if absent.
3. Run the [preflight script](Preflight-And-Postflight-Scripts), then reload
   configuration — preflight is allowed to rewrite the repo URL and client
   identifier.
4. Retrieve and flatten the [manifests](Manifests), then de-duplicate items.
5. Load the [catalogs](Using-Catalogs).
6. Validate and clean the [download cache](The-Download-Cache).
7. Status-check every item and bucket it into install, update, uninstall or
   loop-suppressed — see [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed).
8. Apply the `--item` filter if one was given, then resolve
   [dependencies](Dependencies-And-Update-Chains).
9. **A `--checkonly` run stops here**, writes `InstallInfo.yaml` and returns 0.
10. Apply the deferral filters: install window,
    [blocking applications](Blocking-Applications), and in auto mode the
    active-user check.
11. Download, then install, then uninstall.
12. Write `InstallInfo.yaml` and the reports, run the postflight script, and end
    the session.

Section banners in the log mark these phases: `PREFLIGHT EXECUTION`,
`MANIFEST RETRIEVAL`, `CATALOG LOADING`, `STATUS CHECKING`,
`DOWNLOADING PACKAGES`, `INSTALLING PACKAGES`, `POSTFLIGHT EXECUTION`,
`SESSION COMPLETE`. See [Logging](Logging).

## Common invocations

See what the client would do, without changing anything:

```
managedsoftwareupdate --checkonly
```

The same, with verbose output for troubleshooting:

```
managedsoftwareupdate -vv --checkonly
```

Print the configuration the client is actually using, including which repo URL
and client identifier it resolved:

```
managedsoftwareupdate --show-config
```

Do a full run by hand, with the progress window:

```
managedsoftwareupdate --auto --show-status
```

Work on one item only, which is the fastest way to test a new pkgsinfo:

```
managedsoftwareupdate --auto --item "Example App"
```

Test a manifest that is not yet published, from a local file:

```
managedsoftwareupdate --local-only-manifest C:\temp\test-manifest.yaml --checkonly
```

Inspect and clear loop suppression after fixing the pkgsinfo that caused it:

```
managedsoftwareupdate --loop-status
```

```
managedsoftwareupdate --clear-loop "Example App"
```

Inspect and reclaim the download cache:

```
managedsoftwareupdate --cache-status
```

```
managedsoftwareupdate --clean-cache
```

Verify the installation is intact:

```
managedsoftwareupdate --self-check
```

## Notes and limitations

- `--quiet` / `-q` is accepted by the parser and has no effect whatsoever. There
  is no way to silence output.
- `--version` / `-V` only prints the version when it is the *only* argument.
  `managedsoftwareupdate --version --auto` performs a full auto run.
- There is no `--verbose=N` form. Verbosity is counted from repeated `v`
  characters, and `-v=2` is not recognised.
- The exit-immediately flags are evaluated before the single-instance mutex is
  taken, in a fixed order: `--show-config`, `--set-bootstrap-mode`,
  `--clear-bootstrap-mode`, `--cache-status`, `--validate-cache`,
  `--clean-cache`, `--clear-loop`, `--loop-status`, `--selfupdate-status`,
  `--perform-selfupdate`, `--check-selfupdate`, `--clear-selfupdate`,
  `--restart-service`, `--self-check`, `--preflight-only`, `--postflight-only`.
  If you combine two of them, the earlier one in that list wins and the rest are
  ignored.
- The preflight and postflight scripts have **no timeout**. A script that hangs
  hangs the run.
- A postflight script that exits non-zero produces a warning only. It never
  changes the session result or the exit code.
- Neither script runs on an unhandled exception, and postflight does not run in
  `--checkonly`.

## See also

- [How Cimian Runs](How-Cimian-Runs)
- [Client Configuration](Client-Configuration)
- [How Cimian Decides What Needs To Be Installed](How-Cimian-Decides-What-Needs-To-Be-Installed)
- [Install Loop Prevention](Install-Loop-Prevention)
- [Preflight And Postflight Scripts](Preflight-And-Postflight-Scripts)
- [The Download Cache](The-Download-Cache)
- [Logging](Logging)
- [cimiwatcher](cimiwatcher)
- [cimitrigger](cimitrigger)
