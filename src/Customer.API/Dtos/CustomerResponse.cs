namespace Customer.API.Dtos;

/// <summary>
/// A customer profile, returned by every endpoint and read by order-api through Refit: keep it
/// flat and stable.
/// </summary>
public sealed record CustomerResponse(
    int Id,
    int UserId,
    string FirstName,
    string LastName,
    string Telephone,
    string Email,
    string Address);
