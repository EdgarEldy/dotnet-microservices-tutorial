namespace Microsoft.Extensions.Hosting;

// Shared entry point every service calls first in its Program.cs.
// OpenTelemetry, health checks and HTTP resilience are wired here in feature/infrastructure.
public static class Extensions
{
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        return builder;
    }
}
