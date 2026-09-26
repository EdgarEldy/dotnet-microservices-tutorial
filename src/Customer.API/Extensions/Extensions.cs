using Common.Lib.Exceptions;
using Customer.API.Data;
using Customer.API.Filters;
using Customer.API.Services;
using FluentValidation;
using Microsoft.OpenApi;

namespace Customer.API.Extensions;

/// <summary>
/// Every registration of customer-api, grouped here (the dotnet/eShop convention) so that
/// Program.cs stays a short, readable pipeline.
/// </summary>
public static class Extensions
{
    private const string DatabaseConnectionName = "customer-db";

    public static IHostApplicationBuilder AddApplicationServices(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        // Aspire client integration: connection string injected by AppHost (WithReference(customerDb)),
        // plus retries, health check and tracing. customer-db is the only database this service knows.
        builder.AddNpgsqlDbContext<AppDbContext>(DatabaseConnectionName);
        services.AddHostedService<DatabaseInitializer>();

        // JWT validation and the RESOURCE:ACTION permission policies, shared through ServiceDefaults.
        builder.AddDefaultAuthentication();

        services.AddScoped<ICustomerService, CustomerService>();

        services.AddValidatorsFromAssemblyContaining<Program>();

        // Errors: every exception becomes a ProblemDetails through Common.Lib's handler.
        services.AddProblemDetails();
        services.AddExceptionHandler<GlobalExceptionHandler>();

        services.AddControllers(options => options.Filters.Add<ValidationFilter>());
        services.AddSwagger();

        return builder;
    }

    private static void AddSwagger(this IServiceCollection services)
    {
        const string bearerScheme = "Bearer";

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "Customer.API", Version = "v1" });
            options.AddSecurityDefinition(bearerScheme, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Access token returned by POST /api/v1/Auth/Login.",
            });
            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(bearerScheme, document)] = [],
            });
        });
    }
}
