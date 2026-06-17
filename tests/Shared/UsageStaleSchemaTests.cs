using Cimian.Core.Services;
using Xunit;
using YamlDotNet.Serialization;

namespace Cimian.Tests.Shared;

/// <summary>
/// Schema tests for unused_software_removal_info (removal_days + paths,
/// plus Cimian's minimum_history_days extension).
/// The same YAML structure must round-trip through every model that carries
/// it: cimiimport's PkgsInfo, makecatalogs' PkgsInfo, and the engine's
/// CatalogItem. A field landing in one model but not another silently strips
/// it during the import → catalog → client pipeline, so each model gets its
/// own deserialization pin.
/// </summary>
public class UsageStaleSchemaTests
{
    private const string PkginfoYaml = """
        name: StaleApp
        version: '1.0'
        unattended_uninstall: true
        unused_software_removal_info:
          removal_days: 30
          paths:
          - C:\Program Files\StaleApp\staleapp.exe
          - C:\Program Files\StaleApp\helper.exe
          minimum_history_days: 14
        """;

    private static IDeserializer PlainDeserializer() => new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    [Fact]
    public void CimiimportPkgsInfo_Deserializes_UnusedRemovalInfo()
    {
        var pkg = PlainDeserializer().Deserialize<Cimian.CLI.Cimiimport.Models.PkgsInfo>(PkginfoYaml);

        Assert.Equal(30, pkg.UnusedSoftwareRemovalInfo?.RemovalDays);
        Assert.Equal(2, pkg.UnusedSoftwareRemovalInfo?.Paths?.Count);
        Assert.Equal(14, pkg.UnusedSoftwareRemovalInfo?.MinimumHistoryDays);
    }

    [Fact]
    public void MakecatalogsPkgsInfo_Deserializes_UnusedRemovalInfo()
    {
        var pkg = PlainDeserializer().Deserialize<Cimian.CLI.Makecatalogs.Models.PkgsInfo>(PkginfoYaml);

        Assert.Equal(30, pkg.UnusedSoftwareRemovalInfo?.RemovalDays);
        Assert.Equal(2, pkg.UnusedSoftwareRemovalInfo?.Paths?.Count);
        Assert.Equal(14, pkg.UnusedSoftwareRemovalInfo?.MinimumHistoryDays);
    }

    [Fact]
    public void EngineCatalogItem_Deserializes_UnusedRemovalInfo()
    {
        var item = PlainDeserializer().Deserialize<Cimian.CLI.managedsoftwareupdate.Models.CatalogItem>(PkginfoYaml);

        Assert.True(item.UnattendedUninstall);
        Assert.Equal(30, item.UnusedSoftwareRemovalInfo?.RemovalDays);
        Assert.Equal(2, item.UnusedSoftwareRemovalInfo?.Paths?.Count);
        Assert.Equal(14, item.UnusedSoftwareRemovalInfo?.MinimumHistoryDays);
    }

    [Fact]
    public void EngineCatalogItem_FieldsDefaultNull_WhenAbsent()
    {
        // Absence must read as "feature disabled" — null is the canonical
        // not-configured signal for the engine and for reporting.
        const string minimal = """
            name: PlainApp
            version: '1.0'
            """;

        var item = PlainDeserializer().Deserialize<Cimian.CLI.managedsoftwareupdate.Models.CatalogItem>(minimal);

        Assert.Null(item.UnusedSoftwareRemovalInfo);
    }

    [Fact]
    public void MakecatalogsPkgsInfo_RoundTrips_UnusedRemovalInfo()
    {
        var pkg = PlainDeserializer().Deserialize<Cimian.CLI.Makecatalogs.Models.PkgsInfo>(PkginfoYaml);
        var yaml = new SerializerBuilder().Build().Serialize(pkg);
        var again = PlainDeserializer().Deserialize<Cimian.CLI.Makecatalogs.Models.PkgsInfo>(yaml);

        Assert.Equal(30, again.UnusedSoftwareRemovalInfo?.RemovalDays);
        Assert.Equal(pkg.UnusedSoftwareRemovalInfo?.Paths, again.UnusedSoftwareRemovalInfo?.Paths);
        Assert.Equal(14, again.UnusedSoftwareRemovalInfo?.MinimumHistoryDays);
    }
}

/// <summary>
/// Contract pins for NoOpUsageDataSource: every answer must push callers
/// down their fail-safe path (skip the stale-usage pass entirely).
/// </summary>
public class NoOpUsageDataSourceTests
{
    private readonly NoOpUsageDataSource _source = new();

    [Fact]
    public void IsAvailable_IsFalse() => Assert.False(_source.IsAvailable);

    [Fact]
    public void TryGetLastUsed_ReturnsFalse_ForAnyPath()
    {
        Assert.False(_source.TryGetLastUsed(@"C:\Program Files\Any\any.exe", out var lastUsed));
        Assert.Equal(default, lastUsed);
    }

    [Fact]
    public void GetHistoryDays_IsZero() => Assert.Equal(0, _source.GetHistoryDays());

    [Fact]
    public void GetDataFreshnessDays_IsMaxValue_SoAnyStalenessThresholdRejects()
        => Assert.Equal(int.MaxValue, _source.GetDataFreshnessDays());
}
