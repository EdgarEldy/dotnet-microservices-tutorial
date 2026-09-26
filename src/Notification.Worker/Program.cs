using Microsoft.AspNetCore.Builder;
using Notification.Worker.Extensions;

// A consumer-only worker still runs on WebApplication rather than the generic host,
// so it can serve /health/live and /health/ready like every other service.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddApplicationServices();

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();
