using System.Globalization;
using Common.Lib.Exceptions;
using Confluent.Kafka;
using Contracts;
using FluentValidation;
using Identity.API.Data;
using Identity.API.Filters;
using Identity.API.Messaging;
using Identity.API.Models;
using Identity.API.Security;
using Identity.API.Services;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

namespace Identity.API.Extensions;

/// <summary>
/// Every registration of identity-api, grouped here (the dotnet/eShop convention) so that
/// Program.cs stays a short, readable pipeline.
/// </summary>
public static class Extensions
{
    private const string DatabaseConnectionName = "identity-db";
    private const string KafkaConnectionName = "kafka";

    public static IHostApplicationBuilder AddApplicationServices(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        services.AddSingleton(TimeProvider.System);

        // Aspire client integration: connection string injected by AppHost (WithReference(identityDb)),
        // plus retries, health check and tracing.
        builder.AddNpgsqlDbContext<AppDbContext>(DatabaseConnectionName);

        // Hosted services start one after the other, in registration order: the EF migrations
        // (DatabaseInitializer) run first, then MassTransit's SQL transport migration (registered
        // in AddMessaging, before AddMassTransit), then the bus itself.
        services.AddHostedService<DatabaseInitializer>();

        // The key ring protecting Identity's confirmation and reset tokens lives in identity-db,
        // so a token issued before a restart, or by another instance, still validates.
        services.AddDataProtection()
            .SetApplicationName("identity-api")
            .PersistKeysToDbContext<AppDbContext>();

        builder.AddIdentity();
        builder.AddJwtAuthentication();
        builder.AddMessaging();

        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddSingleton<IJwtService, JwtService>();

        services.AddValidatorsFromAssemblyContaining<Program>();

        // Errors: this service's 401 mapping first, then Common.Lib's handler for everything else.
        services.AddProblemDetails();
        services.AddExceptionHandler<AuthenticationFailedExceptionHandler>();
        services.AddExceptionHandler<GlobalExceptionHandler>();

        services.AddControllers(options => options.Filters.Add<ValidationFilter>());
        services.AddSwagger();

        return builder;
    }

    private static void AddIdentity(this IHostApplicationBuilder builder)
    {
        // AddIdentityCore rather than AddIdentity: no cookie scheme, this API only speaks JWT.
        builder.Services
            .AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedEmail = true;

                options.Password.RequiredLength = 8;

                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

                // Short JWT claim names from the start: the claims principal factory emits them,
                // the token carries them, JwtBearer (MapInboundClaims = false) reads them back.
                options.ClaimsIdentity.UserIdClaimType = AppClaimTypes.UserId;
                options.ClaimsIdentity.UserNameClaimType = AppClaimTypes.UserName;
                options.ClaimsIdentity.EmailClaimType = AppClaimTypes.Email;
                options.ClaimsIdentity.RoleClaimType = AppClaimTypes.Role;
            })
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders()
            // After AddRoles, which registers Identity's own role-aware factory.
            .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>();
    }

    private static void AddJwtAuthentication(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<JwtOptions>()
            .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddScoped<RevokedTokenJwtBearerEvents>();

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured from the bound JwtOptions (not read eagerly), so the key AppHost injects,
        // or a test's configuration, is the one used.
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;

                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = jwt.CreateSigningKey(),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    NameClaimType = AppClaimTypes.UserName,
                    RoleClaimType = AppClaimTypes.Role,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                bearer.EventsType = typeof(RevokedTokenJwtBearerEvents);
            });

        builder.Services.AddAuthorization();
    }

    private static void AddMessaging(this IHostApplicationBuilder builder)
    {
        // The SQL transport's queues live in identity-db (never shared with another service),
        // in their own "transport" schema, next to the EF-managed tables in "public". Read from
        // the Aspire-injected connection string when the options are first resolved.
        builder.Services.AddOptions<SqlTransportOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                options.ConnectionString = configuration.GetConnectionString(DatabaseConnectionName);
                options.Schema = "transport";
                // PostgreSQL roles are server-wide: a service-specific name keeps the grants of
                // each service's transport schema apart.
                options.Role = "identity_api_transport";
            });

        // MassTransit's own migrator for the transport schema, role and functions. Registered
        // before AddMassTransit so it completes before the bus starts. The database itself is
        // created by AppHost (AddDatabase), not here.
        builder.Services.AddPostgresMigrationHostedService(options =>
        {
            options.CreateDatabase = false;
            options.CreateSchema = true;
            options.CreateInfrastructure = true;
        });

        builder.Services.AddMassTransit(x =>
        {
            // Transactional outbox: IPublishEndpoint.Publish in a request scope writes to
            // OutboxMessage through AppDbContext, committed with the business rows; the delivery
            // service relays committed rows to the bus afterwards.
            x.AddEntityFrameworkOutbox<AppDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
            });

            // The outbox delivers to the bus, not to Kafka (a rider): this consumer produces
            // each delivered event to its Kafka topic. See IdentityEventsKafkaRelay.
            x.AddConsumer<IdentityEventsKafkaRelay>();

            // Durable bus: MassTransit's PostgreSQL transport, in identity-db's "transport" schema
            // (SqlTransportOptions below). A delivered outbox message stays in that queue until
            // the relay has produced it to Kafka, and survives a crash or a Kafka outage.
            x.UsingPostgres((context, cfg) =>
            {
                // Kafka down: a few quick retries in process, then the message goes back to the
                // SQL queue and is redelivered later. After the last interval it lands in the
                // relay's _error queue, still in identity-db, for manual redelivery.
                cfg.UseDelayedRedelivery(redelivery => redelivery.Intervals(
                    TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
                    TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
                    TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(8)));
                cfg.UseMessageRetry(retry => retry.Intervals(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)));
                cfg.ConfigureEndpoints(context);
            });

            x.AddRider(rider =>
            {
                // Fail a produce after 30 s instead of librdkafka's 5 min default, so an outage
                // turns into a retry/redelivery quickly rather than a long-held SQL lock.
                var producerConfig = new ProducerConfig { MessageTimeoutMs = 30_000 };

                // Keyed by user id: the events of one user land on one partition, in order.
                rider.AddProducer<string, UserRegisteredEvent>(KafkaTopics.UserRegistered, producerConfig,
                    context => context.Message.UserId.ToString(CultureInfo.InvariantCulture));
                rider.AddProducer<string, PasswordResetRequestedEvent>(KafkaTopics.PasswordResetRequested, producerConfig,
                    context => context.Message.UserId.ToString(CultureInfo.InvariantCulture));

                rider.UsingKafka((context, kafka) =>
                {
                    // Bootstrap servers injected by AppHost (WithReference(kafka)), read when the
                    // bus starts rather than at registration.
                    var connectionString = context.GetRequiredService<IConfiguration>().GetConnectionString(KafkaConnectionName)
                        ?? throw new InvalidOperationException(
                            $"Connection string '{KafkaConnectionName}' is missing: run identity-api through AppHost.");
                    kafka.Host(connectionString);
                });
            });
        });
    }

    private static void AddSwagger(this IServiceCollection services)
    {
        const string bearerScheme = "Bearer";

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "Identity.API", Version = "v1" });
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
