using Contracts;
using Moq;
using Notification.Worker.Consumers;
using Notification.Worker.Services;
using Notification.Worker.Tests.TestSupport;

namespace Notification.Worker.Tests.Consumers;

public sealed class OrderCreatedEventConsumerTests
{
    [Fact]
    public async Task Consume_ShouldDelegateToOrderNotificationService_WhenOrderCreatedEventIsReceived()
    {
        using var cts = new CancellationTokenSource();
        var order = new OrderCreatedEvent(12, 7, "jane.doe@example.com", "Jane Doe", 42, "Mechanical Keyboard", 2, 59.90m);
        var service = new Mock<IOrderNotificationService>(MockBehavior.Strict);
        service.Setup(s => s.ConfirmOrderAsync(order, cts.Token)).Returns(Task.CompletedTask);

        await new OrderCreatedEventConsumer(service.Object).Consume(ConsumeContexts.For(order, cts.Token));

        service.Verify(s => s.ConfirmOrderAsync(order, cts.Token), Times.Once);
        service.VerifyNoOtherCalls();
    }
}
