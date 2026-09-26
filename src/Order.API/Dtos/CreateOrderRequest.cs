namespace Order.API.Dtos;

/// <summary>
/// A new order. Both ids are validated synchronously against catalog-api and customer-api; the
/// Idempotency-Key travels as a header, not in the body.
/// </summary>
public sealed record CreateOrderRequest(int CustomerId, int ProductId, int Quantity);
