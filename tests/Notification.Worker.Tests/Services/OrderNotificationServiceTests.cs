using Contracts;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Notification.Worker.Services;

namespace Notification.Worker.Tests.Services;

/// <summary>
/// The Saga decision in isolation: exactly one outcome event per order, NotificationFailedEvent
/// only for an EmailDeliveryException, any other failure propagated (retried) with nothing produced.
/// </summary>
public sealed class OrderNotificationServiceTests
{
    private static readonly OrderCreatedEvent Order =
        new(12, 7, "jane.doe@example.com", "Jane Doe", 42, "Mechanical Keyboard", 2, 59.90m);

    private readonly Mock<IEmailNotification> emailNotification = new(MockBehavior.Strict);

    private readonly Mock<ITopicProducer<OrderConfirmedEvent>> orderConfirmedProducer = new();

    private readonly Mock<ITopicProducer<NotificationFailedEvent>> notificationFailedProducer = new();

    private readonly OrderNotificationService service;

    public OrderNotificationServiceTests() =>
        service = new OrderNotificationService(
            emailNotification.Object,
            orderConfirmedProducer.Object,
            notificationFailedProducer.Object,
            NullLogger<OrderNotificationService>.Instance);

    [Fact]
    public async Task ConfirmOrderAsync_ShouldProduceOrderConfirmedEventOnly_WhenEmailIsSent()
    {
        using var cts = new CancellationTokenSource();
        emailNotification.Setup(email => email.SendOrderConfirmationAsync(Order, cts.Token)).Returns(Task.CompletedTask);

        await service.ConfirmOrderAsync(Order, cts.Token);

        emailNotification.Verify(email => email.SendOrderConfirmationAsync(Order, cts.Token), Times.Once);
        orderConfirmedProducer.Verify(
            producer => producer.Produce(new OrderConfirmedEvent(Order.OrderId), cts.Token), Times.Once);
        orderConfirmedProducer.VerifyNoOtherCalls();
        notificationFailedProducer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConfirmOrderAsync_ShouldProduceNotificationFailedEventOnly_WhenEmailDeliveryFails()
    {
        using var cts = new CancellationTokenSource();
        const string reason = "Simulated failure: the confirmation e-mail could not be sent.";
        emailNotification.Setup(email => email.SendOrderConfirmationAsync(Order, cts.Token))
            .ThrowsAsync(new EmailDeliveryException(reason));

        await service.ConfirmOrderAsync(Order, cts.Token);

        notificationFailedProducer.Verify(
            producer => producer.Produce(new NotificationFailedEvent(Order.OrderId, reason), cts.Token), Times.Once);
        notificationFailedProducer.VerifyNoOtherCalls();
        orderConfirmedProducer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConfirmOrderAsync_ShouldPropagateExceptionAndProduceNothing_WhenEmailFailsWithAnotherException()
    {
        var failure = new InvalidOperationException("SMTP server unreachable");
        emailNotification.Setup(email => email.SendOrderConfirmationAsync(Order, It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ConfirmOrderAsync(Order, TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown);
        orderConfirmedProducer.VerifyNoOtherCalls();
        notificationFailedProducer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConfirmOrderAsync_ShouldPropagateException_WhenProducingTheOutcomeFails()
    {
        emailNotification.Setup(email => email.SendOrderConfirmationAsync(Order, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        orderConfirmedProducer.Setup(producer => producer.Produce(It.IsAny<OrderConfirmedEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Kafka unreachable"));

        await Assert.ThrowsAsync<TimeoutException>(
            () => service.ConfirmOrderAsync(Order, TestContext.Current.CancellationToken));

        notificationFailedProducer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConfirmOrderAsync_ShouldThrowArgumentNullException_WhenOrderIsNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.ConfirmOrderAsync(null!, TestContext.Current.CancellationToken));

        orderConfirmedProducer.VerifyNoOtherCalls();
        notificationFailedProducer.VerifyNoOtherCalls();
    }
}
