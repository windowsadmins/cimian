# CI build timing benchmark — `build.ps1 -Sign`

**Purpose:** baseline data for planning a `github.com/windowsadmins/cimian` GitHub Actions pipeline.

## Reference run — 2026-04-28

Local Windows host (developer workstation, warm caches, AOT publish + signing + packaging for both arches).

| Phase | Wall time |
|---|---|
| .NET solution build (15 csproj across CLI / GUI / shared / tests) | ~25 s |
| `dotnet publish` win-x64 — 11 tools, ReadyToRun + IL trim + self-contained | ~4 m 45 s |
| `dotnet publish` win-arm64 — same 11 tools | ~4 m 35 s |
| Authenticode signing — 44 binaries (22 per arch) via `signtool` | ~14 s |
| MSI packaging — `Cimian-x64.msi` (cimipkg embeds 626-file CAB, 291 MB payload) | ~2 m 5 s |
| MSI packaging — `Cimian-arm64.msi` | ~1 m 55 s |
| `.nupkg` packaging — both arches | ~1 m 30 s |
| `.pkg` packaging — both arches (~150 MB each) | ~3 m 20 s |
| Cleanup of prior-version artifacts | seconds |
| **Total** | **19 m 33 s (1173 s)** |

Artifacts produced: 2× `Cimian-*.msi` (~280-300 MB each), 2× `CimianTools-*.nupkg` (~285-305 MB), 2× `CimianTools-*.pkg` (~150-160 MB).

## Where the time goes

~75 % of wall time is `dotnet publish` × 11 tools × 2 arches with AOT/IL trim + ReadyToRun. The remaining 25 % is mostly MSI/.pkg packaging, which is single-threaded inside `cimipkg` (CAB compression of the publish trees).

Signing is fast — sub-15 s for all 44 binaries — so it's not a meaningful target for optimization.

## Implications for GitHub Actions

### Expected runtime on hosted runners

`windows-latest` runners are 4-core / 16 GB. Expect **25-35 min cold** for an equivalent workload, possibly more if the AOT publish step is bottlenecked on disk I/O. The reference machine is faster than a hosted runner across all dimensions.

### Levers worth considering

1. **Matrix x64 + arm64 publish in parallel jobs.** Cuts wall time roughly 40 % (the tool-publishing phase is the long pole) at the cost of doubling CI minutes. If feedback latency on PRs matters more than minutes, this is the highest-impact change.
2. **Cross-compile only.** arm64 publishes from x64 today via `dotnet publish -r win-arm64`. There's no native arm64 runner in `windows-latest`; a self-hosted arm64 runner would help only if matrixing is also adopted.
3. **NuGet + obj cache.** `actions/cache` on `~\.nuget\packages` and per-project `obj/` cuts NuGet restore + incremental build by ~30-60 s on warm runs. Cheap and uncontroversial.
4. **Drop AOT/trim for non-shipping binaries** (test runners, internal tools that don't ship in MSIs). Could save 1-2 min if a meaningful number of tools qualify; review `Cimian.Tests.csproj` and any internal-only CLI projects first.
5. **Self-hosted Windows runner** with persistent SDK + NuGet cache lands closest to the reference 19 m 33 s. Best path if "fleet-grade rebuild speed" matters more than runner cost.
6. **Split the workflow.** A "build + test" job that gates merges (skip MSI/.pkg/.nupkg packaging) finishes in ~10-12 min on hosted runners. A separate "package + sign + release" job runs on `main` push or tag and produces the artifacts. Keeps PR feedback fast without sacrificing the full pipeline on releases.

### Code-signing in CI

Local builds pull the cert from Azure Key Vault via `.githooks/post-checkout`. For GitHub Actions:

- Use the `azure/login@v2` action with a federated identity (OIDC) scoped to `cimian-repo-secrets` Key Vault — no long-lived secrets in repo.
- Fetch via `az keyvault secret download` at workflow start, import into `Cert:\CurrentUser\My`, run `signtool` as today, then revoke the runner's local cert at job teardown.
- Alternative: switch to **Azure Trusted Signing** for managed code signing without runner-local cert handling. Worth evaluating separately; doesn't affect the timing baseline.

### Recommended starting point

Single hosted-runner workflow with NuGet caching, no matrixing, full pipeline. Measure once, then matrix x64/arm64 if PR latency is unacceptable. ~25-35 min per merge is the baseline to beat.

## How to reproduce

```powershell
Set-Location packages\CimianTools
$start = Get-Date
.\build.ps1 -Sign 2>&1 | Tee-Object -FilePath "$env:TEMP\cimian-fullbuild.log"
$duration = (Get-Date) - $start
"Total: $($duration.TotalMinutes.ToString('F1')) min ($($duration.TotalSeconds.ToString('F0')) s)"
```

Phase-by-phase breakdown in the log; grep for `Publishing`, `Signing`, `MSI package created`, `pkg package created`, `Build completed in`.
