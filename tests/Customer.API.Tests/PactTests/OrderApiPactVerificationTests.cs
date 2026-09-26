using Customer.API.Data;
using Customer.API.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PactNet;
using PactNet.Verifier;

namespace Customer.API.Tests.PactTests;

/// <summary>
/// Replays every interaction of the committed pacts/order-api-customer-api.json against the real
/// customer-api (full pipeline, JWT validation and the ownership rule included, PostgreSQL behind
/// it). The verifier sends a token for <see cref="CallerUserId"/>, and the seeded customer belongs
/// to that user, because customer-api only returns the caller's own profile.
/// </summary>
public sealed class OrderApiPactVerificationTests(CustomerApiFactory factory) : IClassFixture<CustomerApiFactory>
{
    private const int ExistingCustomerId = 1;
    private const int MissingCustomerId = 999;

    /// <summary>The user order-api's caller is, well above the ids CustomerApiFactory.NewUserId hands out.</summary>
    private const int CallerUserId = 900_000;

    [Fact]
    public async Task GetCustomer_ShouldSatisfyTheOrderApiPact_WhenVerifiedAgainstTheCommittedContract()
    {
        var (server, address) = PactProvider.Start(factory, new Dictionary<string, Func<IServiceProvider, CancellationToken, Task>>
        {
            ["a customer with id 1 exists"] = EnsureCallerCustomerExistsAsync,
            ["no customer with id 999"] = EnsureCustomerIsMissingAsync,
        });
        await using var _ = server;

        // The token order-api forwards: a Customer who may read customer profiles.
        var token = TestTokens.Create(
            server.Services, CallerUserId, CustomerApiFactory.CustomerRole, CustomerApiFactory.ReadPermission);

        using var verifier = new PactVerifier("customer-api", new PactVerifierConfig
        {
            LogLevel = PactLogLevel.Warn,
            Outputters = [new PactProvider.XunitOutput()],
        });

        verifier
            .WithHttpEndpoint(address)
            .WithFileSource(new FileInfo(PactProvider.PactFile("order-api", "customer-api")))
            .WithProviderStateUrl(new Uri(address, PactProvider.ProviderStatesPath))
            .WithCustomHeader("Authorization", $"Bearer {token}")
            .Verify();
    }

    private static async Task EnsureCallerCustomerExistsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var dbContext = services.GetRequiredService<AppDbContext>();
        if (await dbContext.Customers.AnyAsync(
                c => c.Id == ExistingCustomerId && c.UserId == CallerUserId, cancellationToken))
        {
            return;
        }

        // Customer 1 and the caller's profile must be one and the same row (UserId is unique).
        await dbContext.Customers
            .Where(c => c.Id == ExistingCustomerId || c.UserId == CallerUserId)
            .ExecuteDeleteAsync(cancellationToken);

        dbContext.Customers.Add(new Models.Customer
        {
            Id = ExistingCustomerId,
            UserId = CallerUserId,
            FirstName = "Ada",
            LastName = "Lovelace",
            Telephone = "+33600000000",
            Email = "ada@example.com",
            Address = "1 Analytical Engine Street",
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureCustomerIsMissingAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        await services.GetRequiredService<AppDbContext>().Customers
            .Where(c => c.Id == MissingCustomerId)
            .ExecuteDeleteAsync(cancellationToken);
}
