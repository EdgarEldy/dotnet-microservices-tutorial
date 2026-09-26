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

app.UseRateLimiter();

app.UseMiddleware<JwtValidationMiddleware>();

app.MapReverseProxy();

app.MapDefaultEndpoints();

app.Run();
