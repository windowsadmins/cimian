using Microsoft.Extensions.Logging.Abstractions;
using Cimian.Core.Models;
using Cimian.Engine.Predicates;
using Xunit;

namespace Cimian.Tests;

/// <summary>
/// The condition language reads a literal list after IN (#149), rejects anything it
/// cannot parse instead of ignoring it (#149), and gives LIKE NSPredicate's anchored
/// wildcard match instead of a substring test (#151).
/// </summary>
public class ConditionParsingTests
{
    private readonly PredicateEngine _engine = new(NullLogger<PredicateEngine>.Instance);

    private Task<bool> Evaluate(string condition, string hostname = "TEST-PC", string domain = "WORKGROUP") =>
        _engine.EvaluateConditionAsync(condition, new SystemFacts { Hostname = hostname, Domain = domain, Architecture = "x64" });

    [Theory]
    [InlineData("domain IN [\"CORP\", \"EDU\"]", "CORP", true)]
    [InlineData("domain IN [\"CORP\", \"EDU\"]", "EDU", true)]
    [InlineData("domain IN [\"CORP\", \"EDU\"]", "edu", true)]
    [InlineData("domain IN [\"CORP\", \"EDU\"]", "RESEARCH", false)]
    [InlineData("domain IN ['CORP','EDU','RESEARCH']", "RESEARCH", true)]
    [InlineData("domain IN {\"CORP\", \"EDU\"}", "EDU", true)]
    [InlineData("domain IN [CORP, EDU]", "EDU", true)]
    [InlineData("domain IN [\"Value, with comma\", \"EDU\"]", "Value, with comma", true)]
    [InlineData("domain IN \"CORP,EDU\"", "EDU", true)]
    [InlineData("domain IN [\"CORP\", \"EDU\"] AND arch == \"x64\"", "EDU", true)]
    [InlineData("domain IN [\"CORP\", \"EDU\"] AND arch == \"arm64\"", "EDU", false)]
    public async Task In_WithList_MatchesEveryValue(string condition, string domain, bool expected)
    {
        Assert.Equal(expected, await Evaluate(condition, domain: domain));
    }

    [Theory]
    [InlineData("domain == \"CORP\" \"EDU\"")]
    [InlineData("domain == \"CORP\" )")]
    [InlineData("domain IN [\"CORP\", \"EDU\"")]
    [InlineData("domain IN [\"CORP\" \"EDU\"]")]
    [InlineData("domain IN [\"CORP\",]")]
    [InlineData("domain == \"CORP\" ; arch == \"x64\"")]
    public void MalformedCondition_IsAParseError(string condition)
    {
        Assert.Throws<ParseException>(() => new ExpressionParser().Parse(condition));
    }

    [Fact]
    public async Task MalformedCondition_DoesNotMatch()
    {
        // Before, the trailing value was ignored and this matched CORP.
        Assert.False(await Evaluate("domain == \"CORP\" \"EDU\"", domain: "CORP"));
    }

    [Theory]
    [InlineData("hostname CONTAINS ENG-LAB", "ENG-LAB-01", true)]
    [InlineData("hostname CONTAINS ENG-LAB", "ENG-STUDIO-01", false)]
    [InlineData("hostname = 'LAB-01'", "LAB-01", true)]
    [InlineData("domain == corp.local", "CORP.LOCAL", true)]
    public async Task BareValuesWithHyphensDotsAndSingleEquals_AreRead(string condition, string value, bool expected)
    {
        var hostnameOrDomain = condition.StartsWith("domain") ? await Evaluate(condition, domain: value) : await Evaluate(condition, hostname: value);
        Assert.Equal(expected, hostnameOrDomain);
    }

    [Theory]
    [InlineData("LAB*", "LAB-01", true)]
    [InlineData("LAB*", "MY-LAB-01", false)]
    [InlineData("*LAB", "MY-LAB", true)]
    [InlineData("*LAB", "LAB-01", false)]
    [InlineData("*LAB*", "MY-LAB-01", true)]
    [InlineData("LAB", "LAB", true)]
    [InlineData("LAB", "LAB-01", false)]
    [InlineData("LAB", "lab", true)]
    [InlineData("LAB-0?", "LAB-01", true)]
    [InlineData("LAB-0?", "LAB-012", false)]
    [InlineData("LAB.*", "LAB.X", true)]
    [InlineData("LAB.*", "LABX", false)]
    [InlineData("A+B(1)", "A+B(1)", true)]
    public async Task Like_IsAnAnchoredWildcardMatch(string pattern, string hostname, bool expected)
    {
        Assert.Equal(expected, await Evaluate($"hostname LIKE '{pattern}'", hostname: hostname));
    }
}
