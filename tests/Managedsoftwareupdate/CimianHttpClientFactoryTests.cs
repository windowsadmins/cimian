using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests for CimianHttpClientFactory - the headers every request of a client carries.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public class CimianHttpClientFactoryTests : IDisposable
{
    private readonly TextWriter _originalOut = Console.Out;
    private readonly StringWriter _stdout = new();
    private readonly string _testDir = Path.Combine(Path.GetTempPath(), "CimianTests", Guid.NewGuid().ToString());

    public CimianHttpClientFactoryTests()
    {
        Directory.CreateDirectory(_testDir);
        Console.SetOut(_stdout);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        try
        {
            Directory.Delete(_testDir, recursive: true);
        }
        catch { /* Ignore cleanup errors */ }
    }

    private static HttpClient CreateClient(params string[] additionalHeaders)
    {
        var config = new CimianConfig
        {
            AuthToken = "token",
            AdditionalHttpHeaders = additionalHeaders.ToList()
        };
        return CimianHttpClientFactory.CreateHttpClient(config);
    }

    private HttpClient CreateClientFromYaml(string yaml)
    {
        var path = Path.Combine(_testDir, "Config.yaml");
        File.WriteAllText(path, yaml);
        var config = new ConfigurationService(policyRegistryPath: null).LoadConfig(path);
        return CimianHttpClientFactory.CreateHttpClient(config);
    }

    private static string[] HeaderValues(HttpClient client, string name)
        => client.DefaultRequestHeaders.TryGetValues(name, out var values) ? values.ToArray() : [];

    private static string[] HeaderNames(HttpClient client)
        => client.DefaultRequestHeaders.Select(h => h.Key).Order().ToArray();

    [Fact]
    public void CreateHttpClient_AdditionalHttpHeaders_AddsEachHeader()
    {
        var client = CreateClient("X-Client-Serial: ABC123", "X-Client-Id: 9F1E0B6C");

        Assert.Equal(["ABC123"], HeaderValues(client, "X-Client-Serial"));
        Assert.Equal(["9F1E0B6C"], HeaderValues(client, "X-Client-Id"));
    }

    [Fact]
    public void CreateHttpClient_AdditionalHttpHeaders_SplitsAtTheFirstColonAndTrims()
    {
        var client = CreateClient("  X-Client-Time :  10:30:15  ");

        Assert.Equal(["10:30:15"], HeaderValues(client, "X-Client-Time"));
    }

    [Fact]
    public void CreateHttpClient_AdditionalHttpHeaders_LaterEntryReplacesAnEarlierOneOfAnyCase()
    {
        var client = CreateClient("X-Client-Id: first", "x-client-id: second");

        Assert.Equal(["second"], HeaderValues(client, "X-Client-Id"));
    }

    [Fact]
    public void CreateHttpClient_AdditionalHttpHeaders_EmptyValue_IsSent()
    {
        var client = CreateClient("X-Client-Flag:");

        Assert.Equal([""], HeaderValues(client, "X-Client-Flag"));
    }

    [Fact]
    public void CreateHttpClient_AdditionalHttpHeaders_Null_AddsNothing()
    {
        var config = new CimianConfig { AuthToken = "token", AdditionalHttpHeaders = null! };

        var client = CimianHttpClientFactory.CreateHttpClient(config);

        Assert.Equal(HeaderNames(CreateClient()), HeaderNames(client));
    }

    [Fact]
    public void CreateHttpClient_AuthorizationEntry_IsSkippedWithAWarning()
    {
        var client = CreateClient("authorization: Bearer other");

        var authorization = Assert.Single(HeaderValues(client, "Authorization"));
        Assert.DoesNotContain("other", authorization);
        Assert.Contains("entry 1", _stdout.ToString());
    }

    [Fact]
    public void CreateHttpClient_UserAgentEntry_IsSkippedWithAWarning()
    {
        var client = CreateClient("User-Agent: other");

        Assert.Equal(["Cimian-ManagedSoftwareUpdate/1.0"], HeaderValues(client, "User-Agent"));
        Assert.Contains("entry 1", _stdout.ToString());
    }

    [Theory]
    [InlineData("Bearer s3cret")]
    [InlineData(": s3cret")]
    [InlineData("   : s3cret")]
    [InlineData("Bad Name: s3cret")]
    [InlineData("Content-Type: s3cret")]
    [InlineData(null)]
    public void CreateHttpClient_UnusableEntry_IsSkippedWithAWarning(string? entry)
    {
        var client = CreateClient("X-Client-Id: kept", entry!);

        Assert.Equal(HeaderNames(CreateClient("X-Client-Id: kept")), HeaderNames(client));
        Assert.Contains("entry 2", _stdout.ToString());
        Assert.DoesNotContain("s3cret", _stdout.ToString());
    }

    [Theory]
    [InlineData("X-Client-First: 1\nX-Client-Second: 2")]
    [InlineData("X-Client-First: 1\rX-Client-Second: 2")]
    [InlineData("X-Client-First: 1\r\nX-Client-Second: 2")]
    [InlineData("X-Client-First\nX-Client-Second: 2")]
    [InlineData("X-Client-First: 1\0")]
    [InlineData("X-Client-First: a\u001bb")]
    [InlineData("X-Client-First: a\u007fb")]
    [InlineData("X-Client-First: caf\u00e9")]
    [InlineData("X-Client-First: a\u0085b")]
    public void CreateHttpClient_ControlOrNonAsciiCharacter_IsSkippedWithAWarning(string entry)
    {
        var client = CreateClient("X-Client-Id: kept", entry);

        Assert.Equal(HeaderNames(CreateClient("X-Client-Id: kept")), HeaderNames(client));
        Assert.Contains("entry 2", _stdout.ToString());
        Assert.Contains("printable ASCII", _stdout.ToString());
    }

    [Fact]
    public void CreateHttpClient_PrintableAsciiAndTabInTheValue_AreSent()
    {
        var value = "a b\tc " + string.Concat(Enumerable.Range('!', '~' - '!' + 1).Select(i => (char)i));

        var client = CreateClient("X-Client-Text: " + value);

        Assert.Equal([value], HeaderValues(client, "X-Client-Text"));
    }

    [Fact]
    public void CreateHttpClient_LineBreakAroundTheEntry_IsTrimmedAway()
    {
        var client = CreateClient("\r\nX-Client-Id: 9F1E0B6C\r\n");

        Assert.Equal(["9F1E0B6C"], HeaderValues(client, "X-Client-Id"));
    }

    [Theory]
    [InlineData("  - \"X-Client-First: 1\\nX-Client-Second: 2\"")]
    [InlineData("  - |\n    X-Client-First: 1\n    X-Client-Second: 2")]
    public void CreateHttpClient_YamlEntryWithALineBreak_IsSkippedAndTheOthersAreKept(string entry)
    {
        // A double-quoted \n escape and a block scalar each make one entry that holds a line break.
        var client = CreateClientFromYaml($@"
AdditionalHttpHeaders:
  - ""X-Client-Id: 9F1E0B6C""
{entry}
  - ""X-Client-Serial: ABC123""
");

        Assert.Equal(["9F1E0B6C"], HeaderValues(client, "X-Client-Id"));
        Assert.Equal(["ABC123"], HeaderValues(client, "X-Client-Serial"));
        Assert.Empty(HeaderValues(client, "X-Client-First"));
        Assert.Empty(HeaderValues(client, "X-Client-Second"));
        Assert.Contains("entry 2", _stdout.ToString());
    }

    [Fact]
    public void CreateHttpClient_YamlBlockScalarOfSeveralLines_IsSkippedWithAWarning()
    {
        var client = CreateClientFromYaml(@"
AdditionalHttpHeaders: |
  X-Client-First: 1
  X-Client-Second: 2
");

        Assert.Empty(HeaderValues(client, "X-Client-First"));
        Assert.Empty(HeaderValues(client, "X-Client-Second"));
        Assert.Contains("entry 1", _stdout.ToString());
    }
}
