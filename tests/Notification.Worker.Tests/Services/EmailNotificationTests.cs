using Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Moq;
using Notification.Worker.Services;

namespace Notification.Worker.Tests.Services;

/// <summary>
/// The logged "e-mails": a token is a single-use credential, so no log record (message or
/// structured state) ever carries it in full. The order confirmation fails on purpose for a
/// configured product name.
/// </summary>
public sealed class EmailNotificationTests
{
    private const string LongToken = "Q2ZESjhKc2VjcmV0LXRva2VuLXZhbHVlLTAxMjM0NTY3ODk";

    private readonly FakeLogger<EmailNotification> logger = new();

    private readonly EmailNotification emailNotification;

    public EmailNotificationTests()
    {
        var options = new Mock<IOptionsMonitor<NotificationOptions>>();
        options.Setup(monitor => monitor.CurrentValue)
            .Returns(new NotificationOptions { FailingProductNames = ["Broken Product"] });

        emailNotification = new EmailNotification(options.Object, logger);
    }

    [Fact]
    public async Task SendAccountActivationAsync_ShouldLogTruncatedTokenOnly_WhenTokenIsLong()
    {
        var registration = new UserRegisteredEvent(5, "new.user@example.com", LongToken);

        await emailNotification.SendAccountActivationAsync(registration, TestContext.Current.CancellationToken);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Contains("new.user@example.com", record.Message, StringComparison.Ordinal);
        Assert.Contains($"token={LongToken[..6]}...", record.Message, StringComparison.Ordinal);
        AssertTokenNeverLogged(record, LongToken);
    }

    [Fact]
    public async Task SendPasswordResetAsync_ShouldLogTruncatedTokenOnly_WhenTokenIsLong()
    {
        var reset = new PasswordResetRequestedEvent(5, "forgetful@example.com", LongToken);

        await emailNotification.SendPasswordResetAsync(reset, TestContext.Current.CancellationToken);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Contains("forgetful@example.com", record.Message, StringComparison.Ordinal);
        Assert.Contains($"{LongToken[..6]}...", record.Message, StringComparison.Ordinal);
        AssertTokenNeverLogged(record, LongToken);
    }

    [Theory]
    [InlineData("Zq9Xw")]
    [InlineData("Zq9Xw7")]
    public async Task SendAccountActivationAsync_ShouldNotLogAnyTokenCharacter_WhenTokenIsNotLongerThanVisiblePrefix(string token)
    {
        await emailNotification.SendAccountActivationAsync(
            new UserRegisteredEvent(5, "new.user@example.com", token), TestContext.Current.CancellationToken);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Contains("token=...", record.Message, StringComparison.Ordinal);
        AssertTokenNeverLogged(record, token);
    }

    [Theory]
    [InlineData("Zq9Xw")]
    [InlineData("Zq9Xw7")]
    public async Task SendPasswordResetAsync_ShouldNotLogAnyTokenCharacter_WhenTokenIsNotLongerThanVisiblePrefix(string token)
    {
        await emailNotification.SendPasswordResetAsync(
            new PasswordResetRequestedEvent(5, "forgetful@example.com", token), TestContext.Current.CancellationToken);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        AssertTokenNeverLogged(record, token);
    }

    [Fact]
    public async Task SendOrderConfirmationAsync_ShouldLogConfirmation_WhenProductIsNotConfiguredToFail()
    {
        var order = new OrderCreatedEvent(12, 7, "jane.doe@example.com", "Jane Doe", 42, "Mechanical Keyboard", 2, 59.9m);

        await emailNotification.SendOrderConfirmationAsync(order, TestContext.Current.CancellationToken);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Contains("jane.doe@example.com", record.Message, StringComparison.Ordinal);
        Assert.Contains("59.90", record.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Broken Product")]
    [InlineData("broken PRODUCT")]
    public async Task SendOrderConfirmationAsync_ShouldThrowEmailDeliveryException_WhenProductIsConfiguredToFail(string productName)
    {
        var order = new OrderCreatedEvent(13, 7, "jane.doe@example.com", "Jane Doe", 42, productName, 1, 10m);

        var exception = await Assert.ThrowsAsync<EmailDeliveryException>(
            () => emailNotification.SendOrderConfirmationAsync(order, TestContext.Current.CancellationToken));

        Assert.Contains("13", exception.Message, StringComparison.Ordinal);
        Assert.Empty(logger.Collector.GetSnapshot());
    }

    private static void AssertTokenNeverLogged(FakeLogRecord record, string token)
    {
        Assert.DoesNotContain(token, record.Message, StringComparison.Ordinal);
        Assert.All(record.StructuredState ?? [], pair =>
            Assert.DoesNotContain(token, pair.Value ?? string.Empty, StringComparison.Ordinal));
    }
}
