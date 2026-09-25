using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace ServiceDefaults.Tests;

public sealed class ExtensionsHealthCheckTests
{
    private const string LivePath = "/health/live";
    private const string ReadyPath = "/health/ready";

    [Fact]
    public async Task MapDefaultEndpoints_ShouldReturnHealthyOnLive_WhenOnlyDefaultChecksAreRegistered()
    {
        await using var app = await StartAppAsync(Environments.Development);

        var (status, body) = await GetAsync(app, LivePath);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", body);
    }

    [Fact]
    public async Task MapDefaultEndpoints_ShouldReturnHealthyOnReady_WhenOnlyDefaultChecksAreRegistered()
    {
        await using var app = await StartAppAsync(Environments.Development);

        var (status, body) = await GetAsync(app, ReadyPath);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", body);
    }

    [Fact]
    public async Task MapDefaultEndpoints_ShouldReturnHealthyOnReady_WhenEveryRegisteredCheckIsHealthy()
    {
        await using var app = await StartAppAsync(
            Environments.Development,
            checks => checks.AddCheck("database", () => HealthCheckResult.Healthy()));

        var (status, body) = await GetAsync(app, ReadyPath);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", body);
    }

    [Fact]
    public async Task MapDefaultEndpoints_ShouldReturnServiceUnavailableOnReady_WhenAnUntaggedCheckFails()
    {
        await using var app = await StartAppAsync(
            Environments.Development,
            checks => checks.AddCheck("database", () => HealthCheckResult.Unhealthy("database down")));

        var (status, body) = await GetAsync(app, ReadyPath);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("Unhealthy", body);
    }

    [Fact]
    public async Task MapDefaultEndpoints_ShouldKeepLiveHealthy_WhenAnUntaggedCheckFails()
    {
        await using var app = await StartAppAsync(
            Environments.Development,
            checks => checks.AddCheck("database", () => HealthCheckResult.Unhealthy("database down")));

        var (status, body) = await GetAsync(app, LivePath);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", body);
    }

    [Fact]
    public async Task MapDefaultEndpoints_ShouldReturnServiceUnavailableOnLive_WhenACheckTaggedLiveFails()
    {
        await using var app = await StartAppAsync(
            Environments.Development,
            checks => checks.AddCheck("deadlock", () => HealthCheckResult.Unhealthy("stuck"), [Extensions.LiveTag]));

        var (status, body) = await GetAsync(app, LivePath);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("Unhealthy", body);
    }

    [Fact]
    public async Task MapDefaultEndpoints_ShouldReturnServiceUnavailableOnReady_WhenACheckTaggedLiveFails()
    {
        await using var app = await StartAppAsync(
            Environments.Development,
            checks => checks.AddCheck("deadlock", () => HealthCheckResult.Unhealthy("stuck"), [Extensions.LiveTag]));

        var (status, _) = await GetAsync(app, ReadyPath);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
    }

    [Fact]
    public async Task MapDefaultEndpoints_ShouldNotExposeCheckDetails_WhenACheckFails()
    {
        await using var app = await StartAppAsync(
            Environments.Development,
            checks => checks.AddCheck("database", () => HealthCheckResult.Unhealthy("secret connection info")));

        var (_, body) = await GetAsync(app, ReadyPath);

        Assert.DoesNotContain("secret connection info", body);
        Assert.DoesNotContain("database", body);
    }

    [Theory]
    [InlineData("Production", LivePath)]
    [InlineData("Production", ReadyPath)]
    [InlineData("Staging", LivePath)]
    [InlineData("Staging", ReadyPath)]
    public async Task MapDefaultEndpoints_ShouldMapHealthEndpoints_WhenEnvironmentIsNotDevelopment(string environment, string path)
    {
        await using var app = await StartAppAsync(environment);

        var (status, body) = await GetAsync(app, path);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", body);
    }

    [Fact]
    public async Task AddServiceDefaults_ShouldRegisterSelfCheckTaggedLive_WhenCalled()
    {
        await using var app = await StartAppAsync(Environments.Development);

        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>().Value;

        var self = Assert.Single(options.Registrations, r => r.Name == "self");
        Assert.Contains(Extensions.LiveTag, self.Tags);
    }

    private static async Task<WebApplication> StartAppAsync(
        string environment,
        Action<IHealthChecksBuilder>? configureChecks = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
        });
        builder.WebHost.UseTestServer();

        builder.AddServiceDefaults();

        configureChecks?.Invoke(builder.Services.AddHealthChecks());

        var app = builder.Build();
        app.MapDefaultEndpoints();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(WebApplication app, string path)
    {
        using var client = app.GetTestClient();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, body);
    }
}
