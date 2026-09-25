namespace Customer.API.Models;

/// <summary>
/// A customer profile: who a user is business-wise (name, contact, address). UserId is the id of
/// the account in identity-api, stored as a plain value: no foreign key crosses the service
/// boundary, and customer-api never reads identity-api's database.
/// </summary>
public class Customer
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public required string FirstName { get; set; }

    public required string LastName { get; set; }

    public required string Telephone { get; set; }

    public required string Email { get; set; }

    public required string Address { get; set; }
}
