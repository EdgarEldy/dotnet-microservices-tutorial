namespace Catalog.API.Dtos;

/// <summary>
/// A product, flat. Also the body order-api reads through Refit (GET /api/v1/Catalog/Products/{id}):
/// renaming or removing a member is a breaking change for that consumer.
/// </summary>
public record ProductResponse(int Id, int CategoryId, string CategoryName, string ProductName, decimal UnitPrice);
