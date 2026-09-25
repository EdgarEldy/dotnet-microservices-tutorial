using Catalog.API.Models;
using Mapster;

namespace Catalog.API.Dtos;

/// <summary>
/// Entity to DTO mappings that Mapster cannot infer by name. Scanned once at startup into
/// <see cref="TypeAdapterConfig.GlobalSettings"/>, used by both Adapt and ProjectToType (the
/// latter translates it into the SQL join).
/// </summary>
public sealed class CatalogMappingConfig : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<Product, ProductResponse>()
            .Map(dest => dest.CategoryName, src => src.Category.CategoryName);
    }
}
