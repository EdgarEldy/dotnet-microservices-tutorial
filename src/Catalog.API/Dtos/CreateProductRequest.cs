namespace Catalog.API.Dtos;

public record CreateProductRequest(int CategoryId, string ProductName, decimal UnitPrice);
