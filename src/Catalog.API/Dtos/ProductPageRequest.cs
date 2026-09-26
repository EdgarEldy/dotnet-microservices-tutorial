namespace Catalog.API.Dtos;

/// <summary>Query of GET /api/v1/Catalog/Products: a page, optionally within one category.</summary>
public sealed record ProductPageRequest : PageRequest
{
    public int? CategoryId { get; init; }
}
