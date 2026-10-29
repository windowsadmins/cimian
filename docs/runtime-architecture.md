# Runtime architecture and contribution boundaries

Cimian's update client is `cli/managedsoftwareupdate`. The GUI applications start that executable rather than loading an update engine into their own process. This page records the current boundaries and the cleanup proposed in [issue #68](https://github.com/windowsadmins/cimian/issues/68).

## Models used by the client

The live `CatalogItem` and `CimianConfig` types are in `cli/managedsoftwareupdate/Models/UpdateModels.cs`. Catalog parsing, status detection and update execution use those types. They are the starting point for adding a client field and its behavior tests.

The similarly named shared `CatalogItem` and `Configuration` types are unused remnants; [PR #235](https://github.com/windowsadmins/cimian/pull/235) removes them. Do not consolidate onto those models merely because they live under `shared`. The shared `Manifest` type is different: the predicate engine uses it, so it remains.

Moving the live models into a reusable assembly may become useful if there is a second runtime consumer. Such a move should preserve serialization names and existing behavior, with callers migrated together. Removing unused types does not require that extraction.

## The shipped status application

`cimistatus.exe` is the WPF application built from `gui/CimianStatus`. `build.ps1` maps the `cimistatus` tool to that project. The console project formerly under `cli/cimistatus` has the same assembly name but is absent from the publish matrix; [PR #248](https://github.com/windowsadmins/cimian/pull/248) removes that unshipped duplicate.

Managed Software Center is a separate application in `gui/ManagedSoftwareCenter`. Both GUI projects should continue to be built and tested when changing shared client behavior.

## Progress transport

Managed Software Center registers `ProgressServer` as its `IProgressPipeClient` implementation. Despite that interface name, the active transport is loopback TCP, normally port 19848. The client uses the status port passed to `managedsoftwareupdate`; there is no active `CimianProgress` named-pipe server.

The unused `ProgressPipeClient` class is removed by [PR #249](https://github.com/windowsadmins/cimian/pull/249). That cleanup keeps the registered server and existing interface. A transport change needs coordinated sender/receiver changes and integration tests; deleting an unregistered implementation does not change the wire protocol.

## Update execution stays in its own process

`shared/engine` contains predicate evaluation. Run orchestration and the manifest, catalog, download, status and installer services currently live in the `managedsoftwareupdate` executable project. Tests reference that project and use its explicit `InternalsVisibleTo` boundary.

Keep this process boundary for the present cleanup. Running installers or changing machine-wide state inside the interactive GUI would couple its lifetime and privileges to the update run. Extracting a reusable orchestration library would not by itself justify doing that.

A future library extraction should be a separate refactor with these acceptance conditions:

- Preserve the executable entry point, elevation boundary, single-instance behavior, cancellation and reporting order.
- Preserve the GUI's ability to monitor an update process that outlives a window.
- Move one coherent service boundary at a time and run its existing execution-path tests against the new assembly.
- Avoid combining the extraction with changes to manifest precedence, installation policy or progress transport.

There is no supported in-process update-engine API or runtime middleware plugin contract today. Contributions should target the existing executable and service interfaces unless a separate API proposal has been reviewed.
