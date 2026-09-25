using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.ServiceDiscovery;

namespace ServiceDefaults.Tests;

public sealed class ExtensionsHttpClientTests
{
    private const string ClientName = "some-service-client";

    [Fact]
    public async Task AddServiceDefaults_ShouldResolveLogicalServiceName_WhenEndpointIsInConfiguration()
    {
        var handler = new RecordingHandler();
        using var host = BuildHost(handler, new Dictionary<string, string?>
        {
            ["services:some-service:http:0"] = "http://resolved-host:5123",
        });

        var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        using var response = await client.GetAsync("/ping", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var requestUri = Assert.Single(handler.RequestUris);
        Assert.Equal(new Uri("http://resolved-host:5123/ping"), requestUri);
    }

    [Fact]
    public async Task AddServiceDefaults_ShouldPreferHttpsEndpoint_WhenBothSchemesAreInConfiguration()
    {
        var handler = new RecordingHandler();
        using var host = BuildHost(handler, new Dictionary<string, string?>
        {
            ["services:some-service:http:0"] = "http://resolved-host:5123",
            ["services:some-service:https:0"] = "https://resolved-host:7123",
        });

        var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        using var response = await client.GetAsync("/ping", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var requestUri = Assert.Single(handler.RequestUris);
        Assert.Equal(new Uri("https://resolved-host:7123/ping"), requestUri);
    }

    [Fact]
    public async Task AddServiceDefaults_ShouldPassLogicalNameThroughAsHost_WhenServiceIsNotConfigured()
    {
        // Service discovery's pass-through provider: an unknown logical name is not an
        // error, it is handed to DNS as a plain host name (as in a container network).
        var handler = new RecordingHandler();
        using var host = BuildHost(handler, new Dictionary<string, string?>());

        var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        using var response = await client.GetAsync("/ping", TestContext.Current.CancellationToken);

        var requestUri = Assert.Single(handler.RequestUris);
        Assert.Equal("some-service", requestUri.Host);
        Assert.Equal("/ping", requestUri.AbsolutePath);
        Assert.DoesNotContain("+", requestUri.Scheme);
    }

    [Fact]
    public void AddServiceDefaults_ShouldRegisterServiceEndpointResolver_WhenCalled()
    {
        using var host = BuildHost(new RecordingHandler(), new Dictionary<string, string?>());

        Assert.NotNull(host.Services.GetService<ServiceEndpointResolver>());
    }

    [Fact]
    public void AddServiceDefaults_ShouldConfigureStandardResilience_WhenAnHttpClientIsCreated()
    {
        using var host = BuildHost(new RecordingHandler(), new Dictionary<string, string?>());

        var monitor = host.Services.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>();

        // AddStandardResilienceHandler names its options "<client name>-standard".
        var options = monitor.Get($"{ClientName}-standard");
        Assert.True(options.Retry.MaxRetryAttempts > 0);
        Assert.True(options.TotalRequestTimeout.Timeout > TimeSpan.Zero);
    }

    private static IHost BuildHost(RecordingHandler handler, Dictionary<string, string?> configuration)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production,
        });
        builder.Configuration.AddInMemoryCollection(configuration);

        builder.AddServiceDefaults();

        builder.Services.AddHttpClient(ClientName, client => client.BaseAddress = new Uri("https+http://some-service"))
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        return builder.Build();
    }

    // Stands in for the network: records the URI service discovery rewrote the request to.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<Uri> _requestUris = [];

        public IReadOnlyList<Uri> RequestUris => _requestUris;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requestUris)
            {
                _requestUris.Add(request.RequestUri!);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
