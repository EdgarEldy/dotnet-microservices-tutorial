using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PactNet.Infrastructure.Outputters;

namespace Customer.API.Tests.PactTests;

/// <summary>
/// Runs the real service on Kestrel (the Pact verifier is a native process sending real HTTP
/// requests, it cannot reach an in-memory TestServer) with one extra endpoint,
/// POST /provider-states, which the verifier calls before each interaction to set its state up.
/// </summary>
public static class PactProvider
{
    public const string ProviderStatesPath = "/provider-states";

    private const string SolutionFileName = "DotnetMicroservicesTutorial.sln";

    /// <summary>The committed pacts/ folder at the repository root, written by order-api's contract tests.</summary>
    public static string PactFile(string consumer, string provider)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return Path.Combine(directory.FullName, "pacts", $"{consumer}-{provider}.json");
            }
        }

        throw new InvalidOperationException($"No {SolutionFileName} above {AppContext.BaseDirectory}.");
    }

    /// <summary>
    /// Starts a Kestrel-hosted copy of <paramref name="factory"/> whose provider-state endpoint
    /// runs the matching handler (in its own DI scope) for every "setup" call.
    /// </summary>
    public static (WebApplicationFactory<Program> Server, Uri Address) Start(
        WebApplicationFactory<Program> factory,
        IReadOnlyDictionary<string, Func<IServiceProvider, CancellationToken, Task>> states)
    {
        var server = factory.WithWebHostBuilder(builder => builder.ConfigureServices(
            services => services.AddSingleton<IStartupFilter>(new ProviderStatesStartupFilter(states))));
        server.UseKestrel(0);
        server.StartServer();

        var address = server.Services.GetRequiredService<IServer>().Features
            .GetRequiredFeature<IServerAddressesFeature>().Addresses.First();

        // Kestrel reports the wildcard it bound to; the verifier needs a host it can connect to.
        var port = new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal)).Port;
        return (server, new Uri($"http://127.0.0.1:{port}"));
    }

    /// <summary>Sends the verifier's report to the test output.</summary>
    public sealed class XunitOutput : IOutput
    {
        public void WriteLine(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);
    }

    /// <summary>Maps the provider-state endpoint in front of the service's own pipeline (and its authentication).</summary>
    private sealed class ProviderStatesStartupFilter(
        IReadOnlyDictionary<string, Func<IServiceProvider, CancellationToken, Task>> states) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map(ProviderStatesPath, branch => branch.Run(HandleAsync));
            next(app);
        };

        private async Task HandleAsync(HttpContext context)
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var root = body.RootElement;
            var state = root.GetProperty("state").GetString() ?? string.Empty;
            var action = root.TryGetProperty("action", out var value) ? value.GetString() : "setup";

            if (action == "setup")
            {
                if (!states.TryGetValue(state, out var handler))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync($"Unknown provider state '{state}'.", context.RequestAborted);
                    return;
                }

                await using var scope = context.RequestServices.CreateAsyncScope();
                await handler(scope.ServiceProvider, context.RequestAborted);
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
        }
    }
}
