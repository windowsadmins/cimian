# Tests

`Cimian.Tests.csproj` is the single xUnit project for the repository. CI runs it on every
pull request.

| Path | Covers |
|---|---|
| `CLI/Cimiimport/` | cimiimport |
| `CimiTrigger/` | cimitrigger |
| `Cimiwatcher/` | cimiwatcher |
| `Makecatalogs/` | makecatalogs |
| `Makepkginfo/` | makepkginfo |
| `Managedsoftwareupdate/` | managedsoftwareupdate |
| `Manifestutil/` | manifestutil |
| `Shared/` | the shared libraries |
| `*.cs` at the top level | cross-cutting tests: versions, predicates, LoopGuard and others |
| `fixtures/` | sample catalogs, manifests and system facts used by `smoke-test.ps1` |
| `gui-harness/` | UI Automation harness for Managed Software Center; see its README |
| `smoke-test.ps1` | quick checks against built binaries |

## Running the tests

Run the full suite from the repository root:

```powershell
dotnet test tests\Cimian.Tests.csproj -c Release
```

Run one area by filtering on the namespace or class name:

```powershell
dotnet test tests\Cimian.Tests.csproj -c Release --filter "FullyQualifiedName~Makecatalogs"
```

## Smoke test

After `.\build.ps1 -Sign -Binaries`, check that the built binaries start, report a version and
parse their help text:

```powershell
.\tests\smoke-test.ps1
```

It uses `release\<arch>` by default; pass `-BinaryPath` to point it elsewhere.
