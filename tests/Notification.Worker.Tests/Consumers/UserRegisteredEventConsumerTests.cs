using Contracts;
using Moq;
using Notification.Worker.Consumers;
using Notification.Worker.Services;
using Notification.Worker.Tests.TestSupport;

namespace Notification.Worker.Tests.Consumers;

public sealed class UserRegisteredEventConsumerTests
{
    private static readonly UserRegisteredEvent Registration = new(5, "new.user@example.com", "Q29uZmlybWF0aW9uVG9rZW4");

    private readonly Mock<IEmailNotification> emailNotification = new(MockBehavior.Strict);

    [Fact]
    public async Task Consume_ShouldSendActivationEmail_WhenUserRegisteredEventIsReceived()
    {
        using var cts = new CancellationTokenSource();
        emailNotification.Setup(email => email.SendAccountActivationAsync(Registration, cts.Token)).Returns(Task.CompletedTask);

        await new UserRegisteredEventConsumer(emailNotification.Object).Consume(ConsumeContexts.For(Registration, cts.Token));

        emailNotification.Verify(email => email.SendAccountActivationAsync(Registration, cts.Token), Times.Once);
        emailNotification.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Consume_ShouldPropagateException_WhenActivationEmailFails()
    {
        emailNotification.Setup(email => email.SendAccountActivationAsync(Registration, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SMTP server unreachable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new UserRegisteredEventConsumer(emailNotification.Object)
                .Consume(ConsumeContexts.For(Registration, TestContext.Current.CancellationToken)));
    }
}
