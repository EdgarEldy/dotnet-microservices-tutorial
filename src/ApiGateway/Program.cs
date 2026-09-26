using ApiGateway.Extensions;
using ApiGateway.Security;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddApplicationServices();

var app = builder.Build();

// The gateway's own errors become ProblemDetails: exceptions through the registered
// IExceptionHandler, empty error responses (unknown route, no destination) through the status
// code pages. A downstream response is never rewrapped.
app.UseExceptionHandler();
app.UseStatusCodePages();

// Restores the client IP from X-Forwarded-For, for trusted proxies only, before the rate limiter
// partitions by it.
app.UseForwardedHeaders();

app.UseRateLimiter();

app.UseMiddleware<JwtValidationMiddleware>();

app.MapReverseProxy();

app.MapDefaultEndpoints();

app.Run();
