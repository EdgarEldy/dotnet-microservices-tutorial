namespace Order.API.Data;

/// <summary>Sizes shared by the EF Core mapping and the request validation.</summary>
public static class OrderLimits
{
    public const int IdempotencyKeyMaxLength = 100;
    public const int MaxQuantity = 1000;
}
