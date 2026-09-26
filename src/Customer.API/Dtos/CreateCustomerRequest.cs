namespace Customer.API.Dtos;

/// <summary>
/// The caller's own profile. There is no UserId field: it is taken from the access token's "sub"
/// claim, the only way to know the user exists without reading identity-api's database.
/// </summary>
public sealed record CreateCustomerRequest(
    string FirstName,
    string LastName,
    string Telephone,
    string Email,
    string Address);
