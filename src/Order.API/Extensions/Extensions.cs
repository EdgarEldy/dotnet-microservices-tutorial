using Common.Lib.Exceptions;
using Confluent.Kafka;
using Contracts;
using FluentValidation;
using MassTransit;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Order.API.Clients;
using Order.API.Clients.Fallbacks;
using Order.API.Consumers;
using Order.API.Data;
using Order.API.Filters;
using Order.API.Messaging;
using Order.API.Services;
using Refit;

namespace Order.API.Extensions;

/// <summary>
/// Every registration of order-api, grouped here (the dotnet/eShop convention) so that Program.cs
/// stays a short, readable pipeline.
/// </summary>
public static class Extensions
{
    private const string DatabaseConnectionName = "order-db";
    private const string KafkaConnectionName = "kafka";

    // Logical names, resolved by service discovery from the configuration AppHost injects
    // (WithReference(catalogService) / WithReference(customerService)). Never a host:port.
    private const string CatalogBaseAddress = "https+http://catalog-api";
    private const string CustomerBaseAddress = "https+http://customer-api";

    public static IHostApplicationBuilder AddApplicationServices(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        services.AddSingleton(TimeProvider.System);

        // Aspire client integration: order-db's connection string injected by AppHost.
        builder.AddNpgsqlDbContext<AppDbContext>(DatabaseConnectionName);

        // Hosted services start in registration order: EF migrations first, then MassTransit's
        // SQL transport migration (registered in AddMessaging, before AddMassTransit), then the bus.
        services.AddHostedService<DatabaseInitializer>();

        builder.AddDefaultAuthentication();
        builder.AddDownstreamClients();
        builder.AddMessaging();

        services.AddScoped<IOrderService, OrderService>();

        services.AddValidatorsFromAssemblyContaining<Program>();

        services.AddProblemDetails();
        // Handlers run in registration order: the 503 of an unavailable downstream service first,
        // then Common.Lib's handler for everything else.
        services.AddExceptionHandler<DownstreamServiceUnavailableExceptionHandler>();
        services.AddExceptionHandler<GlobalExceptionHandler>();

        services.AddControllers(options => options.Filters.Add<ValidationFilter>());
        services.AddSwagger();

        return builder;
    }

    private static void AddDownstreamClients(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddTransient<ForwardAccessTokenHandler>();

        builder.Services.AddOptions<DownstreamResilienceOptions>()
            .BindConfiguration(DownstreamResilienceOptions.SectionName)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<DownstreamResilienceOptions>, DownstreamResilienceOptionsValidator>();
        builder.Services.AddSingleton<ICircuitBreakerMonitor, CircuitBreakerMonitor>();

        // ServiceDefaults already adds service discovery to every HttpClient. The resilience
        // pipeline goes before the token handler, so every retry sends the caller's token again.
        builder.Services.AddRefitGeneratedClient<IProductClient>()
            .ConfigureHttpClient(client => client.BaseAddress = new Uri(CatalogBaseAddress))
            .AddDownstreamResilience(DownstreamServices.CatalogApi)
            .AddHttpMessageHandler<ForwardAccessTokenHandler>();

        builder.Services.AddRefitGeneratedClient<ICustomerClient>()
            .ConfigureHttpClient(client => client.BaseAddress = new Uri(CustomerBaseAddress))
            .AddDownstreamResilience(DownstreamServices.CustomerApi)
            .AddHttpMessageHandler<ForwardAccessTokenHandler>();
    }

    private static void AddMessaging(this IHostApplicationBuilder builder)
    {
        // Same durable path as identity-api (see OrderEventsKafkaRelay): the SQL transport's queues
        // live in order-db, in their own "transport" schema, never shared with another service.
        builder.Services.AddOptions<SqlTransportOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                options.ConnectionString = configuration.GetConnectionString(DatabaseConnectionName);
                options.Schema = "transport";
                // PostgreSQL roles are server-wide: a service-specific name keeps grants apart.
                options.Role = "order_api_transport";
            });

        builder.Services.AddPostgresMigrationHostedService(options =>
        {
            options.CreateDatabase = false;
            options.CreateSchema = true;
            options.CreateInfrastructure = true;
        });

        builder.Services.AddMassTransit(x =>
        {
            // Transactional outbox: Publish in a request scope writes to OutboxMessage through
            // AppDbContext, committed with the order; delivered to the bus only after the commit.
            x.AddEntityFrameworkOutbox<AppDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
            });

            // Kafka down: quick retries in process, then redelivery from the SQL queue, then the
            // relay's _error queue in order-db for manual redelivery. Configured on the relay only:
            // at bus level it would also apply to the Kafka topic endpoints below, and Kafka cannot
            // reschedule a message, so an exhausted Kafka consumer would crash instead of failing
            // in a controlled way.
            x.AddConsumer<OrderEventsKafkaRelay>(relay =>
            {
                relay.UseDelayedRedelivery(redelivery => redelivery.Intervals(
                    TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
                    TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
                    TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(8)));
                relay.UseMessageRetry(retry => retry.Intervals(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)));
            });

            x.UsingPostgres((context, cfg) => cfg.ConfigureEndpoints(context));

            x.AddRider(rider =>
            {
                var producerConfig = new ProducerConfig { MessageTimeoutMs = 30_000 };

                // Keyed by order id: the events of one order land on one partition, in order.
                rider.AddProducer<string, OrderCreatedEvent>(KafkaTopics.OrderCreated, producerConfig);

                // The Saga's two outcome events, published by notification-worker.
                rider.AddConsumer<OrderConfirmedEventConsumer>();
                rider.AddConsumer<NotificationFailedEventConsumer>();

                rider.UsingKafka((context, kafka) =>
                {
                    var connectionString = context.GetRequiredService<IConfiguration>().GetConnectionString(KafkaConnectionName)
                        ?? throw new InvalidOperationException(
                            $"Connection string '{KafkaConnectionName}' is missing: run order-api through AppHost.");
                    kafka.Host(connectionString);

                    kafka.TopicEndpoint<string, OrderConfirmedEvent>(KafkaTopics.OrderConfirmed, KafkaTopics.ConsumerGroup, endpoint =>
                    {
                        endpoint.AutoOffsetReset = AutoOffsetReset.Earliest;
                        endpoint.CreateIfMissing(topic => topic.NumPartitions = 1);
                        endpoint.UseMessageRetry(retry => retry.Intervals(
                            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)));
                        endpoint.ConfigureConsumer<OrderConfirmedEventConsumer>(context);
                    });

                    kafka.TopicEndpoint<string, NotificationFailedEvent>(KafkaTopics.NotificationFailed, KafkaTopics.ConsumerGroup, endpoint =>
                    {
                        endpoint.AutoOffsetReset = AutoOffsetReset.Earliest;
                        endpoint.CreateIfMissing(topic => topic.NumPartitions = 1);
                        endpoint.UseMessageRetry(retry => retry.Intervals(
                            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)));
                        endpoint.ConfigureConsumer<NotificationFailedEventConsumer>(context);
                    });
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
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "Order.API", Version = "v1" });
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
