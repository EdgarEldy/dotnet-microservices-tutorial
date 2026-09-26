namespace Contracts;

/// <summary>
/// Published by notification-worker once the confirmation e-mail for an order was sent: the
/// Saga's nominal outcome. order-api moves the order from Pending to Confirmed.
/// </summary>
public sealed record OrderConfirmedEvent(int OrderId);
