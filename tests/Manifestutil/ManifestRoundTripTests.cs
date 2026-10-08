using Cimian.CLI.Manifestutil.Models;
using Cimian.CLI.Manifestutil.Services;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Manifestutil;

/// <summary>
/// Editing a manifest with manifestutil must keep every key in it, including the ones
/// PackageManifest does not model.
/// </summary>
public class ManifestRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"manifest_roundtrip_{Guid.NewGuid():N}");
    private readonly ManifestService _service = new();

    public ManifestRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    // Every key the client reads from a manifest, plus keys only people read.
    private const string FullManifest = """
        name: lab
        display_name: Lab machines
        notes: Owned by the lab team
        catalogs:
          - Testing
          - Production
        included_manifests:
          - site_default
        managed_installs:
          - Firefox
        managed_uninstalls:
          - OldTool
        managed_updates:
          - Chrome
        optional_installs:
          - Blender
        default_installs:
          - VLC
        featured_items:
          - Blender
        managed_profiles:
          - WiFi
        managed_apps:
          - CompanyPortal
        conditional_items:
          - condition: hostname BEGINSWITH "LAB-"
            managed_installs:
              - Maya
            conditional_items:
              - condition: arch == "arm64"
                managed_uninstalls:
                  - Maya-x64
          - condition: os_vers_major >= 11
            optional_installs:
              - PowerToys
        """;

    private string WriteManifest(string yaml)
    {
        var path = Path.Combine(_dir, "lab.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private static Dictionary<object, object> Parse(string yaml) =>
        YamlUtils.Deserializer.Deserialize<Dictionary<object, object>>(yaml);

    [Fact]
    public void LoadAndSave_KeepsEveryKey()
    {
        var path = WriteManifest(FullManifest);

        _service.SaveManifest(path, _service.GetManifest(path));

        var before = Parse(FullManifest);
        var after = Parse(File.ReadAllText(path));
        Assert.Equal(before.Keys.Cast<string>().Order(), after.Keys.Cast<string>().Order());
        foreach (var key in before.Keys)
        {
            Assert.Equal(
                YamlUtils.Serializer.Serialize(before[key]),
                YamlUtils.Serializer.Serialize(after[key]));
        }
    }

    [Fact]
    public void AddingAPackage_KeepsEveryOtherKey()
    {
        var path = WriteManifest(FullManifest);
        var manifest = _service.GetManifest(path);

        _service.AddPackageToManifest(manifest, "Inkscape", ManifestSection.ManagedInstalls);
        _service.SaveManifest(path, manifest);

        var after = Parse(File.ReadAllText(path));
        Assert.Equal(new object[] { "Firefox", "Inkscape" }, (List<object>)after["managed_installs"]);
        foreach (var key in new[] { "display_name", "notes", "default_installs", "featured_items",
                     "managed_profiles", "managed_apps", "conditional_items" })
        {
            Assert.True(after.ContainsKey(key), $"{key} was dropped");
        }

        var conditional = (List<object>)after["conditional_items"];
        Assert.Equal(2, conditional.Count);
        var nested = (List<object>)((Dictionary<object, object>)conditional[0])["conditional_items"];
        Assert.Equal("arch == \"arm64\"", ((Dictionary<object, object>)nested[0])["condition"]);
    }

    [Fact]
    public void RemovingTheLastPackage_DropsThatSectionOnly()
    {
        var path = WriteManifest(FullManifest);
        var manifest = _service.GetManifest(path);

        _service.RemovePackageFromManifest(manifest, "OldTool", ManifestSection.ManagedUninstalls);
        _service.SaveManifest(path, manifest);

        var after = Parse(File.ReadAllText(path));
        Assert.False(after.ContainsKey("managed_uninstalls"));
        Assert.True(after.ContainsKey("conditional_items"));
        Assert.True(after.ContainsKey("default_installs"));
    }

    [Fact]
    public void Save_KeepsTheKeyOrderOfTheFile()
    {
        var path = WriteManifest(FullManifest);

        _service.SaveManifest(path, _service.GetManifest(path));

        var order = Parse(File.ReadAllText(path)).Keys.Cast<string>().ToList();
        Assert.Equal(Parse(FullManifest).Keys.Cast<string>().ToList(), order);
    }

    [Fact]
    public void NewManifest_IsWrittenAsBefore()
    {
        var path = Path.Combine(_dir, "new.yaml");

        _service.CreateNewManifest(path, "new");

        Assert.Equal(YamlUtils.SerializeManifest(new PackageManifest { Name = "new" }), File.ReadAllText(path));
    }
}
