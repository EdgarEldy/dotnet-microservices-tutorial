using Refit;

namespace Order.API.Clients;

/// <summary>
/// Refit client for customer-api, base address https+http://customer-api. customer-api only
/// returns the caller's own profile, so a user can only order for their own customer profile.
/// </summary>
public interface ICustomerClient
{
    [Get("/api/v1/Customers/{id}")]
    Task<CustomerDto> GetCustomerAsync(int id, CancellationToken cancellationToken);
}
