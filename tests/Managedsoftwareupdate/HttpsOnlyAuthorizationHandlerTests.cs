using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Xunit;
using Cimian.CLI.managedsoftwareupdate.Services;
using Cimian.Core.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests for HttpsOnlyAuthorizationHandler - the repository credential goes only on https requests.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public class HttpsOnlyAuthorizationHandlerTests : IDisposable
{
    private readonly TextWriter _originalOut = Console.Out;
    private readonly int _originalVerbosity = ConsoleLogger.Verbosity;
    private readonly StringWriter _stdout = new();
    private static readonly AuthenticationHeaderValue Credential = new("Basic", "dXNlcjpwYXNz");

    public HttpsOnlyAuthorizationHandlerTests()
    {
        Console.SetOut(_stdout);
        ConsoleLogger.Verbosity = 1;
        HttpsOnlyAuthorizationHandler.ResetHttpSkipLog();
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        ConsoleLogger.Verbosity = _originalVerbosity;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<AuthenticationHeaderValue?> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static (HttpClient Client, RecordingHandler Inner) CreateClient()
    {
        var inner = new RecordingHandler();
        return (new HttpClient(new HttpsOnlyAuthorizationHandler(Credential, inner)), inner);
    }

    [Fact]
    public async Task Https_Request_CarriesTheCredential()
    {
        var (client, inner) = CreateClient();

        await client.GetAsync("https://repo.example.com/catalogs/Production.yaml");

        Assert.Equal(Credential, Assert.Single(inner.Seen));
    }

    [Theory]
    [InlineData("http://repo.example.com/catalogs/Production.yaml")]
    [InlineData("http://repo.example.com:443/catalogs/Production.yaml")]
    [InlineData("HTTP://repo.example.com/manifests/site_default.yaml")]
    public async Task Http_Request_DoesNotCarryTheCredential(string url)
    {
        var (client, inner) = CreateClient();

        await client.GetAsync(url);

        Assert.Null(Assert.Single(inner.Seen));
    }

    [Fact]
    public async Task UppercaseHttpsScheme_CarriesTheCredential()
    {
        var (client, inner) = CreateClient();

        await client.GetAsync("HTTPS://repo.example.com/pkgs/app.msi");

        Assert.Equal(Credential, Assert.Single(inner.Seen));
    }

    [Fact]
    public void SyncSend_FollowsTheSameRule()
    {
        var (client, inner) = CreateClient();

        client.Send(new HttpRequestMessage(HttpMethod.Get, "https://repo.example.com/a"));
        client.Send(new HttpRequestMessage(HttpMethod.Get, "http://repo.example.com/b"));

        Assert.Equal([Credential, null], inner.Seen);
    }

    [Fact]
    public async Task Http_Skip_IsLoggedOncePerRun_WithoutTheCredential()
    {
        var (client, _) = CreateClient();

        await client.GetAsync("http://cache.example.com/a");
        await client.GetAsync("http://cache.example.com/b");
        var (other, _) = CreateClient();
        await other.GetAsync("http://cache.example.com/c");

        var output = _stdout.ToString();
        var marker = "not sent over plain http";
        Assert.Equal(1, (output.Length - output.Replace(marker, "").Length) / marker.Length);
        Assert.Contains("cache.example.com", output);
        Assert.DoesNotContain("dXNlcjpwYXNz", output);
    }

    [Fact]
    public async Task Https_Request_DoesNotLog()
    {
        var (client, _) = CreateClient();

        await client.GetAsync("https://repo.example.com/a");

        Assert.DoesNotContain("plain http", _stdout.ToString());
    }

    [Fact]
    public async Task ExplicitAuthorizationOnTheRequest_IsLeftAlone()
    {
        var (client, inner) = CreateClient();
        var explicitValue = new AuthenticationHeaderValue("Bearer", "other");
        var request = new HttpRequestMessage(HttpMethod.Get, "https://repo.example.com/a");
        request.Headers.Authorization = explicitValue;

        await client.SendAsync(request);

        Assert.Equal(explicitValue, Assert.Single(inner.Seen));
    }

    [Fact]
    public async Task FollowedRedirect_DropsTheAuthorizationHeader()
    {
        // The handler sees only the first hop, so this relies on HttpClientHandler removing
        // Authorization before it follows a redirect. A loopback server records each hop.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requests = new List<string>();

        var server = Task.Run(async () =>
        {
            for (var hop = 0; hop < 2; hop++)
            {
                using var socket = await listener.AcceptTcpClientAsync();
                var stream = socket.GetStream();
                var buffer = new byte[8192];
                var received = new StringBuilder();
                while (!received.ToString().Contains("\r\n\r\n"))
                {
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0) break;
                    received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                requests.Add(received.ToString());

                var response = hop == 0
                    ? $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/second\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
        });

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/first");
        request.Headers.Authorization = Credential;

        using var result = await client.SendAsync(request);
        await server.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(2, requests.Count);
        Assert.Contains("Authorization:", requests[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization:", requests[1], StringComparison.OrdinalIgnoreCase);
    }
}
