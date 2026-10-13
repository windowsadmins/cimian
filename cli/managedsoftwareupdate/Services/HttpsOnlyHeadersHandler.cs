using System.Net.Http.Headers;
using Cimian.Core.Services;

namespace Cimian.CLI.managedsoftwareupdate.Services;

/// <summary>
/// Adds the repository credential and the AdditionalHttpHeaders entries to a request only
/// when the request goes over https. A plain http request is sent without them, so they
/// never cross the network in clear text, for example to a local caching server that still
/// answers on port 80. Used when AdditionalHttpHeadersHttpsOnly is set.
///
/// Redirects: the handler sits outside HttpClientHandler, so it sees only the first hop.
/// HttpClientHandler removes the Authorization header before following any redirect and
/// refuses to follow one from https to http at all, so a redirected hop never carries
/// the credential.
/// </summary>
public sealed class HttpsOnlyHeadersHandler : DelegatingHandler
{
    private static int _httpSkipLogged;

    private readonly AuthenticationHeaderValue? _authorization;
    private readonly IReadOnlyList<KeyValuePair<string, string>> _headers;

    public HttpsOnlyHeadersHandler(
        AuthenticationHeaderValue? authorization,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _authorization = authorization;
        _headers = headers;
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
        if (!string.Equals(request.RequestUri?.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            if (Interlocked.Exchange(ref _httpSkipLogged, 1) == 0)
            {
                ConsoleLogger.Info($"Repository credential and AdditionalHttpHeaders not sent over plain http ({request.RequestUri?.Host}); they are sent only over https");
            }
            return;
        }

        // A value set explicitly on the request is left alone.
        if (_authorization != null && request.Headers.Authorization == null)
        {
            request.Headers.Authorization = _authorization;
        }

        foreach (var (name, value) in _headers)
        {
            if (!request.Headers.Contains(name))
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }

    /// <summary>Lets a test see the once-per-run log again.</summary>
    internal static void ResetHttpSkipLog() => Interlocked.Exchange(ref _httpSkipLogged, 0);
}
