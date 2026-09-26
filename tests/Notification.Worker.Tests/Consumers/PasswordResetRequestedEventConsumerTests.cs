using Contracts;
using Moq;
using Notification.Worker.Consumers;
using Notification.Worker.Services;
using Notification.Worker.Tests.TestSupport;

namespace Notification.Worker.Tests.Consumers;

public sealed class PasswordResetRequestedEventConsumerTests
{
    private static readonly PasswordResetRequestedEvent Reset = new(5, "forgetful@example.com", "UmVzZXRUb2tlblZhbHVl");

    private readonly Mock<IEmailNotification> emailNotification = new(MockBehavior.Strict);

    [Fact]
    public async Task Consume_ShouldSendPasswordResetEmail_WhenPasswordResetRequestedEventIsReceived()
    {
        using var cts = new CancellationTokenSource();
        emailNotification.Setup(email => email.SendPasswordResetAsync(Reset, cts.Token)).Returns(Task.CompletedTask);

        await new PasswordResetRequestedEventConsumer(emailNotification.Object).Consume(ConsumeContexts.For(Reset, cts.Token));

        emailNotification.Verify(email => email.SendPasswordResetAsync(Reset, cts.Token), Times.Once);
        emailNotification.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Consume_ShouldPropagateException_WhenPasswordResetEmailFails()
    {
        emailNotification.Setup(email => email.SendPasswordResetAsync(Reset, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SMTP server unreachable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PasswordResetRequestedEventConsumer(emailNotification.Object)
                .Consume(ConsumeContexts.For(Reset, TestContext.Current.CancellationToken)));
    }
}
