namespace Customer.API.Data;

/// <summary>Column sizes, shared by the EF Core mapping and the request validators.</summary>
public static class CustomerLimits
{
    public const int NameMaxLength = 100;
    public const int TelephoneMaxLength = 30;
    public const int EmailMaxLength = 256;
    public const int AddressMaxLength = 500;
}
