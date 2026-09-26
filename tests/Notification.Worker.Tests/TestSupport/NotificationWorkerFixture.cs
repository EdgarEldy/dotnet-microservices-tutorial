using System.Collections.Concurrent;
using System.Globalization;
using Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Notification.Worker.Messaging;
using Notification.Worker.Services;
using Testcontainers.Kafka;

namespace Notification.Worker.Tests.TestSupport;

/// <summary>
/// The whole notification-worker (its real Program, MassTransit Kafka rider, consumers and
/// producers) against a real Kafka broker managed by Testcontainers, plus a test-side MassTransit
/// producer that writes events the way order-api and identity-api do. Shared by the Kafka test
/// classes through <see cref="KafkaCollection"/>.
///
/// IEmailNotification is a Moq mock: the order confirmation delegates to the real
/// EmailNotification (so the configured "Broken Product" failure is the production one), while
/// the account e-mails only record their call so that a test can await it.
/// </summary>
public sealed class NotificationWorkerFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly TimeSpan KafkaTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The product name appsettings.json configures as failing.</summary>
    public const string FailingProductName = "Broken Product";

    private readonly KafkaContainer kafka = new KafkaBuilder("confluentinc/cp-kafka:7.5.12").Build();

    private readonly ConcurrentDictionary<int, TaskCompletionSource<UserRegisteredEvent>> activations = new();

    private readonly ConcurrentDictionary<int, TaskCompletionSource<PasswordResetRequestedEvent>> passwordResets = new();

    private EmailNotification? realEmailNotification;

    private KafkaEventProducer? producer;

    public NotificationWorkerFixture()
    {
        EmailNotificationMock
            .Setup(email => email.SendOrderConfirmationAsync(It.IsAny<OrderCreatedEvent>(), It.IsAny<CancellationToken>()))
            .Returns((OrderCreatedEvent order, CancellationToken ct) =>
                realEmailNotification!.SendOrderConfirmationAsync(order, ct));

        EmailNotificationMock
            .Setup(email => email.SendAccountActivationAsync(It.IsAny<UserRegisteredEvent>(), It.IsAny<CancellationToken>()))
            .Callback((UserRegisteredEvent registration, CancellationToken _) =>
                activations.GetOrAdd(registration.UserId, NewCompletionSource<UserRegisteredEvent>).TrySetResult(registration))
            .Returns(Task.CompletedTask);

        EmailNotificationMock
            .Setup(email => email.SendPasswordResetAsync(It.IsAny<PasswordResetRequestedEvent>(), It.IsAny<CancellationToken>()))
            .Callback((PasswordResetRequestedEvent reset, CancellationToken _) =>
                passwordResets.GetOrAdd(reset.UserId, NewCompletionSource<PasswordResetRequestedEvent>).TrySetResult(reset))
            .Returns(Task.CompletedTask);
    }

    public Mock<IEmailNotification> EmailNotificationMock { get; } = new();

    public string KafkaBootstrapServers { get; private set; } = string.Empty;

    public KafkaEventProducer Producer => producer ?? throw new InvalidOperationException("Fixture not initialized.");

    public async ValueTask InitializeAsync()
    {
        await kafka.StartAsync();

        var bootstrap = new Uri(kafka.GetBootstrapAddress());
        KafkaBootstrapServers = $"{bootstrap.Host}:{bootstrap.Port.ToString(CultureInfo.InvariantCulture)}";

        await KafkaTopicReader.CreateTopicsAsync(
            KafkaBootstrapServers,
            KafkaTopics.OrderCreated,
            KafkaTopics.OrderConfirmed,
            KafkaTopics.NotificationFailed,
            KafkaTopics.UserRegistered,
            KafkaTopics.PasswordResetRequested);

        // Starts the worker now: its bus, rider and topic endpoints.
        _ = Services;

        producer = await KafkaEventProducer.StartAsync(KafkaBootstrapServers);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:kafka", KafkaBootstrapServers);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailNotification>();
            services.AddSingleton(provider =>
            {
                realEmailNotification = ActivatorUtilities.CreateInstance<EmailNotification>(provider);
                return EmailNotificationMock.Object;
            });
        });
    }

    /// <summary>Completes once the worker asked for the activation e-mail of <paramref name="userId"/>.</summary>
    public Task<UserRegisteredEvent> ActivationEmailSent(int userId) =>
        activations.GetOrAdd(userId, NewCompletionSource<UserRegisteredEvent>).Task;

    /// <summary>Completes once the worker asked for the password reset e-mail of <paramref name="userId"/>.</summary>
    public Task<PasswordResetRequestedEvent> PasswordResetEmailSent(int userId) =>
        passwordResets.GetOrAdd(userId, NewCompletionSource<PasswordResetRequestedEvent>).Task;

    /// <summary>An id no other test of the run uses, so each test finds its own events on a shared topic.</summary>
    public static int NewId() => Random.Shared.Next(1, int.MaxValue);

    public override async ValueTask DisposeAsync()
    {
        if (producer is not null)
        {
            await producer.DisposeAsync();
        }

        await base.DisposeAsync();
        await kafka.DisposeAsync();
    }

    private static TaskCompletionSource<T> NewCompletionSource<T>(int userId) =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

[CollectionDefinition(Name)]
public sealed class KafkaCollection : ICollectionFixture<NotificationWorkerFixture>
{
    public const string Name = "Kafka";
}
