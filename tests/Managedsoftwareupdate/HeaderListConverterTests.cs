using Xunit;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Cimian.CLI.managedsoftwareupdate.Models;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests for HeaderListConverter - the shapes of AdditionalHttpHeaders it refuses.
/// </summary>
public class HeaderListConverterTests
{
    private static CimianConfig Deserialize(string yaml) => new DeserializerBuilder()
        .WithNamingConvention(PascalCaseNamingConvention.Instance)
        .WithTypeConverter(new HeaderListConverter())
        .IgnoreUnmatchedProperties()
        .Build()
        .Deserialize<CimianConfig>(yaml);

    [Theory]
    [InlineData("AdditionalHttpHeaders:\n  - [a, b]\n", 2, 5)]
    [InlineData("AdditionalHttpHeaders:\n  - X-Client-Serial: ABC123\n    X-Client-Id: 9F1E0B6C\n", 3, 5)]
    [InlineData("AdditionalHttpHeaders:\n  X-Client-Serial: [a, b]\n", 2, 20)]
    public void ReadYaml_UnsupportedShape_FailsAtItsLineAndColumn(string yaml, int line, int column)
    {
        var exception = Assert.ThrowsAny<YamlException>(() => Deserialize(yaml));

        Assert.Equal(line, exception.Start.Line);
        Assert.Equal(column, exception.Start.Column);
    }
}
