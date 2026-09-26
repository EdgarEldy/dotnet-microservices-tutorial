using Contracts;
using Moq;
using Notification.Worker.Tests.TestSupport;

namespace Notification.Worker.Tests.Messaging;

/// <summary>
/// The account events, end to end through Kafka: an event produced on its identity-events topic
/// reaches the worker's consumer, which hands it unchanged to IEmailNotification.
/// </summary>
[Collection(KafkaCollection.Name)]
public sealed class AccountEmailKafkaTests(NotificationWorkerFixture fixture)
{
    [Fact]
    public async Task Consume_ShouldSendActivationEmail_WhenUserRegisteredEventArrivesOnKafka()
    {
        var ct = TestContext.Current.CancellationToken;
        var registration = new UserRegisteredEvent(NotificationWorkerFixture.NewId(), "new.user@example.com", "Q29uZmlybWF0aW9uVG9rZW4");
        var sent = fixture.ActivationEmailSent(registration.UserId);

        await fixture.Producer.ProduceAsync(registration, ct);

        Assert.Equal(registration, await sent.WaitAsync(NotificationWorkerFixture.KafkaTimeout, ct));
        fixture.EmailNotificationMock.Verify(
            email => email.SendAccountActivationAsync(registration, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_ShouldSendPasswordResetEmail_WhenPasswordResetRequestedEventArrivesOnKafka()
    {
        var ct = TestContext.Current.CancellationToken;
        var reset = new PasswordResetRequestedEvent(NotificationWorkerFixture.NewId(), "forgetful@example.com", "UmVzZXRUb2tlblZhbHVl");
        var sent = fixture.PasswordResetEmailSent(reset.UserId);

        await fixture.Producer.ProduceAsync(reset, ct);

        Assert.Equal(reset, await sent.WaitAsync(NotificationWorkerFixture.KafkaTimeout, ct));
        fixture.EmailNotificationMock.Verify(
            email => email.SendPasswordResetAsync(reset, It.IsAny<CancellationToken>()), Times.Once);
    }
}
