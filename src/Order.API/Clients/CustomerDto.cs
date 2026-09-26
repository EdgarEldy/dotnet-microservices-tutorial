namespace Order.API.Clients;

/// <summary>The shape of customer-api's GET /api/v1/Customers/{id}, as order-api consumes it.</summary>
public sealed record CustomerDto(
    int Id,
    int UserId,
    string FirstName,
    string LastName,
    string Telephone,
    string Email,
    string Address);
