namespace Order.API.Clients;

/// <summary>
/// The shape of catalog-api's GET /api/v1/Catalog/Products/{id}, as order-api consumes it
/// (catalog-api's own ProductResponse is never referenced: each service owns its DTOs).
/// </summary>
public sealed record ProductDto(int Id, int CategoryId, string CategoryName, string ProductName, decimal UnitPrice);
