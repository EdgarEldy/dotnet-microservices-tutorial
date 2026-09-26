namespace Contracts;

/// <summary>
/// Published by order-api once an order is committed as Pending; consumed by notification-worker,
/// which sends the confirmation e-mail and answers with <see cref="OrderConfirmedEvent"/> or
/// <see cref="NotificationFailedEvent"/> (the choreographed Saga). Carries only what the e-mail
/// needs: no internal entity.
/// </summary>
public sealed record OrderCreatedEvent(
    int OrderId,
    int CustomerId,
    string CustomerEmail,
    string CustomerName,
    int ProductId,
    string ProductName,
    int Quantity,
    decimal Total);
