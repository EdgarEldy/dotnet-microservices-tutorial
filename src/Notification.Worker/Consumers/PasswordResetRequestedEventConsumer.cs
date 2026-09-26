using Contracts;
using MassTransit;
using Notification.Worker.Services;

namespace Notification.Worker.Consumers;

/// <summary>
/// Sends the password reset e-mail when identity-api accepts a reset request. Fire-and-forget,
/// like the activation e-mail: no compensating event.
/// </summary>
public sealed class PasswordResetRequestedEventConsumer(IEmailNotification emailNotification)
    : IConsumer<PasswordResetRequestedEvent>
{
    public Task Consume(ConsumeContext<PasswordResetRequestedEvent> context) =>
        emailNotification.SendPasswordResetAsync(context.Message, context.CancellationToken);
}
