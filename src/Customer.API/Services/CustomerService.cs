using Common.Lib.Exceptions;
using Customer.API.Data;
using Customer.API.Dtos;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Customer.API.Services;

public sealed class CustomerService(AppDbContext dbContext, ILogger<CustomerService> logger) : ICustomerService
{
    public async Task<CustomerResponse> GetByIdAsync(int id, int userId, bool canReadAnyProfile, CancellationToken cancellationToken)
    {
        var customers = dbContext.Customers.AsNoTracking().Where(c => c.Id == id);

        // A profile holds personal data: a user only reads their own (order-api forwards the
        // caller's token, and a user only orders for themselves). Someone else's profile answers
        // like a missing one, so ids cannot be enumerated.
        if (!canReadAnyProfile)
        {
            customers = customers.Where(c => c.UserId == userId);
        }

        return await customers.ProjectToType<CustomerResponse>().FirstOrDefaultAsync(cancellationToken)
            ?? throw new ResourceNotFoundException(nameof(Models.Customer), id);
    }

    public async Task<CustomerResponse> CreateAsync(int userId, CreateCustomerRequest request, CancellationToken cancellationToken)
    {
        if (await dbContext.Customers.AnyAsync(c => c.UserId == userId, cancellationToken))
        {
            throw DuplicateProfile(userId);
        }

        var customer = new Models.Customer
        {
            UserId = userId,
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            Telephone = request.Telephone.Trim(),
            Email = request.Email.Trim(),
            Address = request.Address.Trim(),
        };
        dbContext.Customers.Add(customer);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        })
        {
            // A concurrent request created this user's profile between the check and the insert.
            throw DuplicateProfile(userId);
        }

        logger.LogInformation("Created customer {CustomerId} for user {UserId}", customer.Id, userId);
        return customer.Adapt<CustomerResponse>();
    }

    public async Task<CustomerResponse> UpdateAsync(int id, int userId, UpdateCustomerRequest request, CancellationToken cancellationToken)
    {
        // Someone else's profile answers exactly like a missing one: its existence is not revealed.
        var customer = await dbContext.Customers
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, cancellationToken)
            ?? throw new ResourceNotFoundException(nameof(Models.Customer), id);

        customer.FirstName = request.FirstName.Trim();
        customer.LastName = request.LastName.Trim();
        customer.Telephone = request.Telephone.Trim();
        customer.Email = request.Email.Trim();
        customer.Address = request.Address.Trim();

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Updated customer {CustomerId}", customer.Id);
        return customer.Adapt<CustomerResponse>();
    }

    private static BusinessRuleException DuplicateProfile(int userId) =>
        new($"User '{userId}' already has a customer profile.");
}
