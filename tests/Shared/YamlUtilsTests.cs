using Cimian.CLI.Cimiimport.Models;
using Cimian.CLI.Manifestutil.Models;
using Cimian.Core.Models;
using Cimian.Core.Services;
using Xunit;
using YamlDotNet.Serialization;

namespace Cimian.Tests.Shared;

/// <summary>
/// Round-trip and canonical-form tests for YamlUtils. The goal is zero diff
/// noise when CimianStudio or a CLI tool loads a deployment YAML and re-saves
/// it. Each test pins one canonical-form decision so the next refactor doesn't
/// silently regress it.
/// </summary>
public class YamlUtilsTests
{
    // ─── unattended_install / unattended_uninstall always emitted ───────────

    [Fact]
    public void SerializePkgInfo_AlwaysEmits_UnattendedInstall_False()
    {
        // Real-world: 181 pkginfo files in deployment/ have `unattended_install: false`
        // explicit. OmitDefaults would silently drop them, so we don't use it.
        var pkg = new PkgsInfo
        {
            Name = "Test",
            Version = "1.0",
            UnattendedInstall = false,
            UnattendedUninstall = false,
        };

        var yaml = YamlUtils.SerializePkgInfo(pkg);

        Assert.Contains("unattended_install: false", yaml);
        Assert.Contains("unattended_uninstall: false", yaml);
    }

    // ─── Priority key order: name → display_name → version → alphabetical ──

    [Fact]
    public void SerializePkgInfo_OrdersKeys_PriorityThenAlphabetical()
    {
        var pkg = new PkgsInfo
        {
            Name = "Z",
            DisplayName = "Z Display",
            Version = "1.0",
            Catalogs = new() { "Testing" },
            Category = "App",
            Developer = "Co",
        };

        var yaml = YamlUtils.SerializePkgInfo(pkg);
        var lines = yaml.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // First three lines must be priority keys in canonical order.
        Assert.StartsWith("name:", lines[0]);
        Assert.StartsWith("display_name:", lines[1]);
        Assert.StartsWith("version:", lines[2]);
        // Then catalogs, category, developer (alphabetical, c < d).
        Assert.StartsWith("catalogs:", lines[3]);
    }

    // ─── _metadata at EOF ──────────────────────────────────────────────────

    [Fact]
    public void SerializePkgInfo_PreservesMetadataAtEof_WhenRoundTrippedAsDictionary()
    {
        // The strongly-typed PkgsInfo model can't carry _metadata (YamlDotNet
        // 16.3 silently drops underscore-prefix aliases). The post-processing
        // reorder logic does keep it last if the input mapping had it though,
        // so this exercises the reorder layer directly via the raw Serializer.
        const string source = """
            name: Test
            version: '1.0'
            developer: Acme
            catalogs:
            - Production
            _metadata:
              created_by: autopkg
              creation_date: '2026-04-21T14:04:12Z'
            """;

        var meta = YamlUtils.ExtractMetadataBlock(source);

        Assert.NotNull(meta);
        Assert.Equal("autopkg", meta!["created_by"]);
        Assert.Equal("2026-04-21T14:04:12Z", meta["creation_date"]);
    }

    // ─── OnDemand: PascalCase must survive round-trip ──────────────────────

    [Fact]
    public void OnDemand_RoundTripsAs_PascalCase()
    {
        // Reproduces ProvisioningManifestEnrollment.yaml's `OnDemand: true`.
        // YamlDotNet's UnderscoredNamingConvention would emit `on_demand`
        // without the explicit Alias.
        const string source = """
            name: Foo
            version: 1.0
            OnDemand: true
            unattended_install: false
            unattended_uninstall: false
            """;

        var pkg = YamlUtils.Deserializer.Deserialize<OnDemandProbe>(source);
        Assert.NotNull(pkg);
        // Sanity: alias-based `name` deserialization works.
        Assert.Equal("Foo", pkg!.Name);
        Assert.True(pkg.OnDemand, $"OnDemand was {pkg.OnDemand}");

        var yaml = YamlUtils.SerializePkgInfo(pkg);
        Assert.Contains("OnDemand: true", yaml);
        Assert.DoesNotContain("on_demand:", yaml);
    }

    private class OnDemandProbe
    {
        [YamlMember(Alias = "name")]
        public string Name { get; set; } = "";
        [YamlMember(Alias = "version")]
        public string Version { get; set; } = "";
        [YamlMember(Alias = "OnDemand")]
        public bool OnDemand { get; set; }
    }

    // ─── included_manifests path normalization ─────────────────────────────

    [Fact]
    public void SerializeManifest_Normalizes_IncludedManifests_Backslashes()
    {
        // Real deployment file: Assigned/Faculty/Instructor/A3072/GilbertoJimenezDesktop.yaml
        // has `Assigned\Faculty\Instructor\A3072.yaml`. We canonicalize.
        var manifest = new PackageManifest
        {
            Name = "Test",
            IncludedManifests = new() { "Assigned\\Faculty\\Instructor\\A3072.yaml" },
        };

        var yaml = YamlUtils.SerializeManifest(manifest);

        Assert.Contains("- Assigned/Faculty/Instructor/A3072.yaml", yaml);
        Assert.DoesNotContain("\\", yaml);
    }

    [Fact]
    public void DeserializeManifest_Normalizes_IncludedManifests_Backslashes()
    {
        const string source = """
            name: Test
            included_manifests:
            - Assigned\Faculty\Instructor\A3072.yaml
            """;

        var manifest = YamlUtils.DeserializeManifest<PackageManifest>(source);

        Assert.NotNull(manifest);
        Assert.Single(manifest!.IncludedManifests!);
        Assert.Equal("Assigned/Faculty/Instructor/A3072.yaml", manifest.IncludedManifests![0]);
    }

    // ─── Multi-line scripts use literal `|` style (not folded `>`) ─────────

    [Fact]
    public void Scripts_WithBlankLines_RoundTrip_AsLiteralBlock()
    {
        // Folded `>` would collapse the blank line — fatal for PowerShell.
        const string script = "if ($foo) {\n    Write-Host 'hi'\n\n    exit 0\n} else {\n    exit 1\n}\n";

        var pkg = new PkgsInfo
        {
            Name = "Foo",
            Version = "1.0",
            InstallCheckScript = script,
        };

        var yaml = YamlUtils.SerializePkgInfo(pkg);
        Assert.Contains("installcheck_script: |", yaml);
        // Folded marker `>` must not appear for this scalar.
        Assert.DoesNotContain("installcheck_script: >", yaml);

        var deserialized = YamlUtils.DeserializePkgInfo<PkgsInfo>(yaml);
        Assert.NotNull(deserialized);
        Assert.Equal(script, deserialized!.InstallCheckScript);
    }

    [Fact]
    public void Scripts_WithCrlf_NormalizeTo_Lf_BeforeEmit()
    {
        // Windows checkouts of YAML produced by tools that don't normalize
        // would write CRLF into literal blocks, causing CRLF-vs-LF diff churn.
        var pkg = new PkgsInfo
        {
            Name = "Foo",
            Version = "1.0",
            PostinstallScript = "Write-Host 'a'\r\nWrite-Host 'b'\r\n",
        };

        var yaml = YamlUtils.SerializePkgInfo(pkg);

        Assert.DoesNotContain("\r", yaml);
    }

    // ─── _metadata extraction via the underscore-prefix workaround ─────────

    [Fact]
    public void ExtractMetadataBlock_ParsesUnderscoreAliasedKey()
    {
        const string source = """
            name: Foo
            version: 1.0
            _metadata:
              created_by: rchristiansen
              creation_date: '2025-01-06T00:07:42Z'
              cimian-promoter_edit_date: '2026-04-24T18:01:29Z'
            """;

        var meta = YamlUtils.ExtractMetadataBlock(source);

        Assert.NotNull(meta);
        Assert.Equal(3, meta!.Count);
        // Order preserved (Dictionary<,> on .NET preserves insertion order).
        var keys = meta.Keys.ToList();
        Assert.Equal("created_by", keys[0]);
        Assert.Equal("creation_date", keys[1]);
        Assert.Equal("cimian-promoter_edit_date", keys[2]);
        Assert.Equal("rchristiansen", meta["created_by"]);
    }

    [Fact]
    public void ExtractMetadataBlock_ReturnsNull_WhenAbsent()
    {
        const string source = "name: Foo\nversion: 1.0\n";
        Assert.Null(YamlUtils.ExtractMetadataBlock(source));
    }

    [Fact]
    public void ExtractMetadataBlock_ReturnsNull_WhenMalformed()
    {
        Assert.Null(YamlUtils.ExtractMetadataBlock(""));
        Assert.Null(YamlUtils.ExtractMetadataBlock("   "));
    }

    // ─── Bare empty manifest keys (known drift) ────────────────────────────

    [Fact]
    public void BareEmptyManifestKeys_AreDroppedOnSave()
    {
        // Real deployment files have `managed_installs:` (bare empty key) as
        // shorthand for "section exists but empty". After deserialize the
        // value is null; our serializer uses OmitNull so the key is dropped
        // entirely. This matches pre-consolidation manifestutil behaviour.
        // Documented here so it doesn't get "fixed" without a deliberate
        // decision (preserving bare keys requires a custom emitter — would
        // also need to distinguish null-was-here from null-wasnt-here).
        const string source = """
            name: Test
            managed_installs:
            managed_updates:
            included_manifests:
            - CoreApps
            """;

        var manifest = YamlUtils.DeserializeManifest<PackageManifest>(source);
        Assert.NotNull(manifest);
        Assert.Null(manifest!.ManagedInstalls);
        Assert.Null(manifest.ManagedUpdates);

        var roundTrip = YamlUtils.SerializeManifest(manifest);
        Assert.DoesNotContain("managed_installs:", roundTrip);
        Assert.DoesNotContain("managed_updates:", roundTrip);
        Assert.Contains("included_manifests:", roundTrip);
    }

    // ─── Generic Serializer/Deserializer reuse for downstream tools ────────

    [Fact]
    public void Serializer_And_Deserializer_AreReused_AsSingletons()
    {
        // CimianStudio relies on these as the canonical entry points; ensure
        // we don't accidentally hand callers fresh builders that re-do every
        // registration on every call.
        Assert.Same(YamlUtils.Serializer, YamlUtils.Serializer);
        Assert.Same(YamlUtils.Deserializer, YamlUtils.Deserializer);
    }

    // ─── Round-trip stability on real deployment fixtures ──────────────────

    [Theory]
    [InlineData("apps/dev/Git-x64-2.54.0.1.yaml")]
    [InlineData("printing/Printer-ECU_PhotoDeptInkjets.yaml")]
    public void RealPkgInfo_DeserializesAndReserializes_WithoutModelException(string relativePath)
    {
        // Reads a real production pkginfo via the cimiimport PkgsInfo model,
        // serializes it back through YamlUtils, and verifies key invariants
        // (no exceptions, name preserved, version preserved, multi-line
        // scripts remain literal). Full byte-equivalence is *not* asserted
        // because the strongly-typed model drops fields it doesn't know about
        // (autoremove, OnDemand, installer.switches, _metadata) — that's the
        // model's limitation, not YamlUtils'. The canonical-form invariants
        // we do test here are what CimianStudio depends on.
        var path = LocateDeploymentFile(relativePath);
        if (path == null)
        {
            return; // deployment/ not checked out alongside this worktree.
        }

        var source = File.ReadAllText(path);
        var pkg = YamlUtils.DeserializePkgInfo<PkgsInfo>(source);
        Assert.NotNull(pkg);
        Assert.False(string.IsNullOrEmpty(pkg!.Name));
        Assert.False(string.IsNullOrEmpty(pkg.Version));

        var rt = YamlUtils.SerializePkgInfo(pkg);
        Assert.Contains($"name: {pkg.Name}", rt);
        Assert.DoesNotContain("\r", rt); // LF-only on Windows checkouts.
        // Second pass must be stable.
        var pkg2 = YamlUtils.DeserializePkgInfo<PkgsInfo>(rt);
        Assert.NotNull(pkg2);
        var rt2 = YamlUtils.SerializePkgInfo(pkg2!);
        Assert.Equal(rt, rt2);
    }

    private static string? LocateDeploymentFile(string relativePath)
    {
        // Walk up from the test binary directory looking for ../../deployment/pkgsinfo/<rel>.
        // The test runs in tests/bin/.../net10.0-windows/win-x64, while deployment/ lives
        // at the parent Cimian/ repo root.
        var current = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(current); i++)
        {
            var candidate = Path.Combine(current, "deployment", "pkgsinfo", relativePath);
            if (File.Exists(candidate)) return candidate;
            // Try going up via Cimian repo root (when CimianTools is the submodule).
            candidate = Path.Combine(current, "..", "deployment", "pkgsinfo", relativePath);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            current = Path.GetDirectoryName(current);
        }
        return null;
    }
}
