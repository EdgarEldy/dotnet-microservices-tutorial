using Order.API.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddApplicationServices();

var app = builder.Build();

// Every error becomes a ProblemDetails: exceptions through the registered IExceptionHandler,
// empty 401/403/404 responses (JwtBearer challenge, forbidden, unknown route) through the
// status code pages.
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapDefaultEndpoints();

app.Run();
