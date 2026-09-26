using Refit;

namespace Order.API.Clients;

/// <summary>
/// Refit client for catalog-api. Its base address is the logical name https+http://catalog-api,
/// resolved by service discovery from the configuration AppHost injects (WithReference(catalogService)).
/// </summary>
public interface IProductClient
{
    [Get("/api/v1/Catalog/Products/{id}")]
    Task<ProductDto> GetProductAsync(int id, CancellationToken cancellationToken);
}
