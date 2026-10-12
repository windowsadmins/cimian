using System.Net.Http.Headers;
using Cimian.Core.Services;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Adds the repository credential to a request only when the request goes over https.
/// A plain http request is sent without it, so the credential never crosses the network
/// in clear text, for example to a local caching server that still answers on port 80.
///
/// Redirects: the handler sits outside HttpClientHandler, so it sees only the first hop.
/// HttpClientHandler removes the Authorization header before following any redirect and
/// refuses to follow one from https to http at all, so a redirected hop never carries
/// the credential.
/// </summary>
public sealed class HttpsOnlyAuthorizationHandler : DelegatingHandler
{
    private static int _httpSkipLogged;

    private readonly AuthenticationHeaderValue _authorization;

    public HttpsOnlyAuthorizationHandler(AuthenticationHeaderValue authorization, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _authorization = authorization;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Apply(request);
        return base.SendAsync(request, cancellationToken);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Apply(request);
        return base.Send(request, cancellationToken);
    }

    private void Apply(HttpRequestMessage request)
    {
        if (request.Headers.Authorization != null)
            return;

        if (string.Equals(request.RequestUri?.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = _authorization;
            return;
        }

        if (Interlocked.Exchange(ref _httpSkipLogged, 1) == 0)
        {
            ConsoleLogger.Info($"Repository credential not sent over plain http ({request.RequestUri?.Host}); it is sent only over https");
        }
    }

    /// <summary>Lets a test see the once-per-run log again.</summary>
    internal static void ResetHttpSkipLog() => Interlocked.Exchange(ref _httpSkipLogged, 0);
}
