using Catalog.API.Data;
using Catalog.API.Filters;
using Catalog.API.Services;
using Common.Lib.Exceptions;
using FluentValidation;
using Mapster;
using Microsoft.OpenApi;

namespace Catalog.API.Extensions;

/// <summary>
/// Every registration of catalog-api, grouped here (the dotnet/eShop convention) so that
/// Program.cs stays a short, readable pipeline.
/// </summary>
public static class Extensions
{
    private const string DatabaseConnectionName = "catalog-db";

    public static IHostApplicationBuilder AddApplicationServices(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        // Aspire client integration: connection string injected by AppHost (WithReference(catalogDb)),
        // plus retries, health check and tracing.
        builder.AddNpgsqlDbContext<AppDbContext>(DatabaseConnectionName);
        services.AddHostedService<DatabaseInitializer>();

        // JWT validation (issuer, audience, lifetime, signature) and the RESOURCE:ACTION
        // permission policies, shared by every business service through ServiceDefaults.
        builder.AddDefaultAuthentication();

        // Entity to DTO mappings Mapster cannot infer by name. Scanning is idempotent.
        TypeAdapterConfig.GlobalSettings.Scan(typeof(Extensions).Assembly);

        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IProductService, ProductService>();

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
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "Catalog.API", Version = "v1" });
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
