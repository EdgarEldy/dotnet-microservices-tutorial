using Customer.API.Dtos;

namespace Customer.API.Services;

public interface ICustomerService
{
    /// <summary>
    /// Reads a profile: the caller's own, or any profile when <paramref name="canReadAnyProfile"/>
    /// (administrators).
    /// </summary>
    Task<CustomerResponse> GetByIdAsync(int id, int userId, bool canReadAnyProfile, CancellationToken cancellationToken);

    /// <summary>Creates the profile of <paramref name="userId"/>; a user has at most one.</summary>
    Task<CustomerResponse> CreateAsync(int userId, CreateCustomerRequest request, CancellationToken cancellationToken);

    /// <summary>Updates a profile, only on behalf of the user who owns it.</summary>
    Task<CustomerResponse> UpdateAsync(int id, int userId, UpdateCustomerRequest request, CancellationToken cancellationToken);
}
