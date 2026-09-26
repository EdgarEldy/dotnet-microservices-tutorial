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
        if (!string.IsNullOrEmpty(authorization))
        {
            request.Headers.TryAddWithoutValidation(HeaderNames.Authorization, authorization);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
