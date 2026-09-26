using System.Globalization;
using Contracts;
using Microsoft.Extensions.Options;

namespace Notification.Worker.Services;

/// <summary>
/// Logs each e-mail instead of sending it. Tokens are single-use credentials, so a log line only
/// ever carries a truncated token and its length, never the full value.
/// </summary>
public sealed partial class EmailNotification(
    IOptionsMonitor<NotificationOptions> options,
    ILogger<EmailNotification> logger) : IEmailNotification
{
    private const int VisibleTokenCharacters = 6;

    public Task SendOrderConfirmationAsync(OrderCreatedEvent order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        cancellationToken.ThrowIfCancellationRequested();

        // The demo's configurable failure: ordering one of these products makes the e-mail fail.
        var failingProductNames = options.CurrentValue.FailingProductNames;
        if (failingProductNames.Contains(order.ProductName, StringComparer.OrdinalIgnoreCase))
        {
            throw new EmailDeliveryException(
                $"Simulated failure: the confirmation e-mail for order {order.OrderId} could not be sent.");
        }

        LogOrderConfirmation(
            order.CustomerEmail,
            order.CustomerName,
            order.OrderId,
            order.Quantity,
            order.ProductName,
            order.Total.ToString("0.00", CultureInfo.InvariantCulture));

        return Task.CompletedTask;
    }

    public Task SendAccountActivationAsync(UserRegisteredEvent registration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();

        var link = string.Create(
            CultureInfo.InvariantCulture,
            $"/api/v1/Auth/ConfirmEmail?userId={registration.UserId}&token={Truncate(registration.ConfirmationToken)}");

        LogAccountActivation(registration.Email, registration.UserId, link, registration.ConfirmationToken.Length);

        return Task.CompletedTask;
    }

    public Task SendPasswordResetAsync(PasswordResetRequestedEvent resetRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resetRequest);
        cancellationToken.ThrowIfCancellationRequested();

        // The token goes back in the body of POST /api/v1/Auth/ResetPassword, hence no link.
        LogPasswordReset(
            resetRequest.Email, resetRequest.UserId, Truncate(resetRequest.ResetToken), resetRequest.ResetToken.Length);

        return Task.CompletedTask;
    }

    private static string Truncate(string token) =>
        token.Length <= VisibleTokenCharacters ? "..." : $"{token[..VisibleTokenCharacters]}...";

    [LoggerMessage(Level = LogLevel.Information,
        Message = "E-mail to {Email}: hello {CustomerName}, your order {OrderId} ({Quantity} x {ProductName}, total {Total}) is confirmed.")]
    private partial void LogOrderConfirmation(
        string email, string customerName, int orderId, int quantity, string productName, string total);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "E-mail to {Email}: activate account {UserId} with {ConfirmationLink} (token truncated, {TokenLength} characters).")]
    private partial void LogAccountActivation(string email, int userId, string confirmationLink, int tokenLength);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "E-mail to {Email}: reset the password of account {UserId} with token {Token} through POST /api/v1/Auth/ResetPassword (token truncated, {TokenLength} characters).")]
    private partial void LogPasswordReset(string email, int userId, string token, int tokenLength);
}
