namespace Notification.Worker.Services;

/// <summary>
/// The "Notification" configuration section.
/// </summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notification";

    /// <summary>
    /// Product names whose order confirmation e-mail fails on purpose (compared case-insensitively),
    /// so the Saga's compensating path (NotificationFailedEvent) can be demonstrated on demand.
    /// </summary>
    public string[] FailingProductNames { get; set; } = [];
}
