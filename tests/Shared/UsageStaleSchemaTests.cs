using Cimian.Core.Services;
using Xunit;
using YamlDotNet.Serialization;

namespace Cimian.Tests.Shared;

/// <summary>
/// Schema tests for the stale-usage removal fields
/// (days_untouched_before_uninstall, usage_tracked_paths,
/// minimum_usage_history_days). The same YAML keys must round-trip through
/// every model that carries them: cimiimport's PkgsInfo, makecatalogs'
/// PkgsInfo, and the engine's CatalogItem. A field landing in one model but
/// not another silently strips it during the import → catalog → client
/// pipeline, so each model gets its own deserialization pin.
/// </summary>
public class UsageStaleSchemaTests
{
    private const string PkginfoYaml = """
        name: StaleApp
        version: '1.0'
        unattended_uninstall: true
        days_untouched_before_uninstall: 30
        usage_tracked_paths:
        - C:\Program Files\StaleApp\staleapp.exe
        - C:\Program Files\StaleApp\helper.exe
        minimum_usage_history_days: 14
        """;

    private static IDeserializer PlainDeserializer() => new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    [Fact]
    public void CimiimportPkgsInfo_Deserializes_UsageStaleFields()
    {
        var pkg = PlainDeserializer().Deserialize<Cimian.CLI.Cimiimport.Models.PkgsInfo>(PkginfoYaml);

        Assert.Equal(30, pkg.DaysUntouchedBeforeUninstall);
        Assert.Equal(2, pkg.UsageTrackedPaths?.Count);
        Assert.Equal(14, pkg.MinimumUsageHistoryDays);
    }

    [Fact]
    public void MakecatalogsPkgsInfo_Deserializes_UsageStaleFields()
    {
        var pkg = PlainDeserializer().Deserialize<Cimian.CLI.Makecatalogs.Models.PkgsInfo>(PkginfoYaml);

        Assert.Equal(30, pkg.DaysUntouchedBeforeUninstall);
        Assert.Equal(2, pkg.UsageTrackedPaths?.Count);
        Assert.Equal(14, pkg.MinimumUsageHistoryDays);
    }

    [Fact]
    public void EngineCatalogItem_Deserializes_UsageStaleFields()
    {
        var item = PlainDeserializer().Deserialize<Cimian.CLI.managedsoftwareupdate.Models.CatalogItem>(PkginfoYaml);

        Assert.True(item.UnattendedUninstall);
        Assert.Equal(30, item.DaysUntouchedBeforeUninstall);
        Assert.Equal(2, item.UsageTrackedPaths?.Count);
        Assert.Equal(14, item.MinimumUsageHistoryDays);
    }

    [Fact]
    public void EngineCatalogItem_FieldsDefaultNull_WhenAbsent()
    {
        // Absence must read as "feature disabled", never zero — the engine
        // treats null/<=0 as opt-out, so a default of 0 is equivalent, but
        // null is the canonical not-configured signal for reporting.
        const string minimal = """
            name: PlainApp
            version: '1.0'
            """;

        var item = PlainDeserializer().Deserialize<Cimian.CLI.managedsoftwareupdate.Models.CatalogItem>(minimal);

        Assert.Null(item.DaysUntouchedBeforeUninstall);
        Assert.Null(item.UsageTrackedPaths);
        Assert.Null(item.MinimumUsageHistoryDays);
    }

    [Fact]
    public void MakecatalogsPkgsInfo_RoundTrips_UsageStaleFields()
    {
        var pkg = PlainDeserializer().Deserialize<Cimian.CLI.Makecatalogs.Models.PkgsInfo>(PkginfoYaml);
        var yaml = new SerializerBuilder().Build().Serialize(pkg);
        var again = PlainDeserializer().Deserialize<Cimian.CLI.Makecatalogs.Models.PkgsInfo>(yaml);

        Assert.Equal(30, again.DaysUntouchedBeforeUninstall);
        Assert.Equal(pkg.UsageTrackedPaths, again.UsageTrackedPaths);
        Assert.Equal(14, again.MinimumUsageHistoryDays);
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
