namespace Order.API.Dtos;

/// <summary>An order as stored by order-api, returned by POST and the paginated list.</summary>
public sealed record OrderResponse(int Id, int CustomerId, int ProductId, int Quantity, decimal Total, string Status);
