namespace Catalog.API.Data;

/// <summary>Column sizes, shared by the EF Core mapping and the request validators.</summary>
public static class CatalogLimits
{
    public const int CategoryNameMaxLength = 100;
    public const int ProductNameMaxLength = 200;

    // numeric(18,2): an amount of money with two decimals.
    public const int UnitPricePrecision = 18;
    public const int UnitPriceScale = 2;
}
