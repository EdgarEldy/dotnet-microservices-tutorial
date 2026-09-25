namespace Customer.API.Dtos;

/// <summary>Full replacement of the editable fields of a profile (PUT semantics).</summary>
public sealed record UpdateCustomerRequest(
    string FirstName,
    string LastName,
    string Telephone,
    string Email,
    string Address);
