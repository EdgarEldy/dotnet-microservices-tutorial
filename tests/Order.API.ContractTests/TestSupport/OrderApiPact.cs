using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Order.API.Clients;
using PactNet;
using Refit;

namespace Order.API.ContractTests.TestSupport;

/// <summary>
/// The consumer side of the contracts: a Pact V4 mock server per provider, and the REAL Refit
/// clients wired the way Order.API's Extensions wires them (AddRefitGeneratedClient + the
/// ForwardAccessTokenHandler), only pointed at the mock server instead of a logical name.
/// </summary>
public static class OrderApiPact
{
    public const string Consumer = "order-api";

    /// <summary>What an incoming order-api request carries; the handler must forward it as is.</summary>
    public const string CallerAuthorization = "Bearer eyJhbGciOiJIUzI1NiJ9.contract-test.signature";

    /// <summary>The Authorization header order-api forwards: any bearer token.</summary>
    public const string BearerTokenRegex = "^Bearer .+$";

    /// <summary>The media types the providers answer with, whatever the charset parameter.</summary>
    public const string JsonContentTypeRegex = @"^application/json(;\s?charset=utf-8)?$";

    public const string ProblemContentTypeRegex = @"^application/problem\+json(;\s?charset=utf-8)?$";

    public static IPactBuilderV4 For(string provider) =>
        Pact.V4(Consumer, provider, new PactConfig
        {
            PactDir = PactFolder.Path,
            LogLevel = PactLogLevel.Warn,
            DefaultJsonSettings = new JsonSerializerOptions(JsonSerializerDefaults.Web),
        }).WithHttpInteractions();

    /// <summary>
    /// Builds the client from a service collection shaped like order-api's, with an ambient
    /// HttpContext carrying the caller's token so the forwarding handler has something to forward.
    /// </summary>
    public static TClient CreateClient<TClient>(Uri mockServerUri)
        where TClient : class
    {
        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        services.AddTransient<ForwardAccessTokenHandler>();
        services.AddRefitGeneratedClient<TClient>()
            .ConfigureHttpClient(client => client.BaseAddress = mockServerUri)
            .AddHttpMessageHandler<ForwardAccessTokenHandler>();

        var provider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[HeaderNames.Authorization] = CallerAuthorization;
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;

        return provider.GetRequiredService<TClient>();
    }
}
