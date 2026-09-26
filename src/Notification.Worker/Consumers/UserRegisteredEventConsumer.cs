using Contracts;
using MassTransit;
using Notification.Worker.Services;

namespace Notification.Worker.Consumers;

/// <summary>
/// Sends the account activation e-mail when identity-api registers a user. Fire-and-forget: a
/// failed activation e-mail does not undo the account, so there is no compensating event.
/// </summary>
public sealed class UserRegisteredEventConsumer(IEmailNotification emailNotification)
    : IConsumer<UserRegisteredEvent>
{
    public Task Consume(ConsumeContext<UserRegisteredEvent> context) =>
        emailNotification.SendAccountActivationAsync(context.Message, context.CancellationToken);
}
