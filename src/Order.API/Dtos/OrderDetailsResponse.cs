using Order.API.Clients;

namespace Order.API.Dtos;

/// <summary>
/// GET /api/v1/Orders/{id}: the order enriched with the product and the customer, read from
/// catalog-api and customer-api through Refit (API Composition). A part is null when its service
/// could not provide it, so one unavailable service does not hide the order itself.
/// </summary>
public sealed record OrderDetailsResponse(
    int Id,
    int CustomerId,
    int ProductId,
    int Quantity,
    decimal Total,
    string Status,
    ProductDto? Product,
    CustomerDto? Customer);
