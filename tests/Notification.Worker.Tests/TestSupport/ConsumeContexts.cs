using MassTransit;
using Moq;

namespace Notification.Worker.Tests.TestSupport;

/// <summary>Builds a mocked MassTransit consume context carrying a message and a cancellation token.</summary>
public static class ConsumeContexts
{
    public static ConsumeContext<T> For<T>(T message, CancellationToken cancellationToken)
        where T : class
    {
        var context = new Mock<ConsumeContext<T>>();
        context.SetupGet(c => c.Message).Returns(message);
        context.SetupGet(c => c.CancellationToken).Returns(cancellationToken);
        return context.Object;
    }
}
