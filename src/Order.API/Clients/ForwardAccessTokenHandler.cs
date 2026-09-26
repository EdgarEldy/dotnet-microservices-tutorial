using System.Net.Http.Headers;
using Microsoft.Net.Http.Headers;

namespace Order.API.Clients;

/// <summary>
/// Forwards the caller's own access token to catalog-api and customer-api, so the downstream
/// services authorize the same user (permissions, customer ownership) instead of trusting
/// order-api blindly. Only used inside an HTTP request; the Saga consumers never call Refit.
/// </summary>
public sealed class ForwardAccessTokenHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var authorization = httpContextAccessor.HttpContext?.Request.Headers[HeaderNames.Authorization].ToString();

        // Set, never appended: the resilience handler runs this handler again on every retry of
        // the same request message, and a second value would turn the retry into a 401.
        request.Headers.Authorization = AuthenticationHeaderValue.TryParse(authorization, out var header) ? header : null;

        return base.SendAsync(request, cancellationToken);
    }
}
