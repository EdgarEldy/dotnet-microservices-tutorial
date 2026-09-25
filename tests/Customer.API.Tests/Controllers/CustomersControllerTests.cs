using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Customer.API.Dtos;
using Customer.API.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Customer.API.Tests.Controllers;

/// <summary>
/// /api/v1/Customers through the real pipeline (JWT, permission policies, validation filter,
/// ProblemDetails) against a real PostgreSQL database.
/// </summary>
public sealed class CustomersControllerTests(CustomerApiFactory factory) : IClassFixture<CustomerApiFactory>
{
    private const string CustomersUrl = "/api/v1/Customers";

    [Fact]
    public async Task GetCustomer_ShouldReturnExactFlatShape_WhenCallerOwnsProfile()
    {
        // The contract order-api's Refit client depends on: exactly these seven camelCase
        // properties, with these JSON types, and nothing else (no envelope, no nesting).
        var userId = CustomerApiFactory.NewUserId();
        var created = await CreateProfileAsync(userId);
        using var client = factory.CreateClientForUser(userId, CustomerApiFactory.ReadPermission);

        using var response = await client.GetAsync($"{CustomersUrl}/{created.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = body.RootElement;
        Assert.Equal(
            ["address", "email", "firstName", "id", "lastName", "telephone", "userId"],
            root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(created.Id, root.GetProperty("id").GetInt32());
        Assert.Equal(userId, root.GetProperty("userId").GetInt32());
        Assert.Equal("Ada", root.GetProperty("firstName").GetString());
        Assert.Equal("Lovelace", root.GetProperty("lastName").GetString());
        Assert.Equal("+44 20 7946 0000", root.GetProperty("telephone").GetString());
        Assert.Equal("ada@example.com", root.GetProperty("email").GetString());
        Assert.Equal("12 St James's Square, London", root.GetProperty("address").GetString());
    }

    [Fact]
    public async Task GetCustomer_ShouldReturnNotFoundProblem_WhenProfileBelongsToAnotherUser()
    {
        // 404, not 403: CUSTOMER:READ alone does not reveal that someone else's profile exists.
        var created = await CreateProfileAsync(CustomerApiFactory.NewUserId());
        using var client = factory.CreateClientForUser(CustomerApiFactory.NewUserId(), CustomerApiFactory.ReadPermission);

        using var response = await client.GetAsync($"{CustomersUrl}/{created.Id}", TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetCustomer_ShouldReturnProfile_WhenCallerIsAdminWithoutOwningIt()
    {
        var ownerId = CustomerApiFactory.NewUserId();
        var created = await CreateProfileAsync(ownerId);
        using var client = factory.CreateClientWithRole(
            CustomerApiFactory.NewUserId(), CustomerApiFactory.AdminRole, CustomerApiFactory.ReadPermission);

        using var response = await client.GetAsync($"{CustomersUrl}/{created.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(created, await response.Content.ReadFromJsonAsync<CustomerResponse>(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetCustomer_ShouldReturnNotFoundProblem_WhenCustomerDoesNotExist()
    {
        using var client = factory.CreateCustomerClient(CustomerApiFactory.NewUserId());

        using var response = await client.GetAsync($"{CustomersUrl}/999999", TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetCustomer_ShouldReturnUnauthorizedProblem_WhenNoTokenIsSent()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{CustomersUrl}/1", TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetCustomer_ShouldReturnForbiddenProblem_WhenTokenLacksReadPermission()
    {
        using var client = factory.CreateClientForUser(CustomerApiFactory.NewUserId(), CustomerApiFactory.WritePermission);

        using var response = await client.GetAsync($"{CustomersUrl}/1", TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateCustomer_ShouldReturnCreatedWithLocationAndTokenUserId_WhenRequestIsValid()
    {
        // The body tries to claim another user's id: the service must ignore it and use "sub".
        var userId = CustomerApiFactory.NewUserId();
        var otherUserId = CustomerApiFactory.NewUserId();
        using var client = factory.CreateCustomerClient(userId);
        using var content = new StringContent(
            $$"""
            {"userId":{{otherUserId}},"firstName":"Grace","lastName":"Hopper","telephone":"555-0100",
             "email":"grace@example.com","address":"1 Navy Way"}
            """,
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(CustomersUrl, content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CustomerResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.True(body.Id > 0);
        Assert.Equal(new CustomerResponse(body.Id, userId, "Grace", "Hopper", "555-0100", "grace@example.com", "1 Navy Way"), body);
        Assert.Equal($"{CustomersUrl}/{body.Id}", response.Headers.Location?.AbsolutePath);

        var stored = await factory.WithDbContextAsync(db =>
            db.Customers.AsNoTracking().SingleAsync(c => c.Id == body.Id, TestContext.Current.CancellationToken));
        Assert.Equal(userId, stored.UserId);
        Assert.False(await factory.WithDbContextAsync(db =>
            db.Customers.AnyAsync(c => c.UserId == otherUserId, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task CreateCustomer_ShouldReturnUnprocessableEntityProblem_WhenUserAlreadyHasProfile()
    {
        var userId = CustomerApiFactory.NewUserId();
        await CreateProfileAsync(userId);
        using var client = factory.CreateCustomerClient(userId);

        using var response = await client.PostAsJsonAsync(CustomersUrl, ValidRequest(), TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Equal(1, await factory.WithDbContextAsync(db =>
            db.Customers.CountAsync(c => c.UserId == userId, TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("not-an-email", "555-0100", "Email")]
    [InlineData("ada@example.com", "call me", "Telephone")]
    public async Task CreateCustomer_ShouldReturnValidationProblem_WhenFieldIsInvalid(
        string email,
        string telephone,
        string invalidProperty)
    {
        var userId = CustomerApiFactory.NewUserId();
        using var client = factory.CreateCustomerClient(userId);
        var request = ValidRequest() with { Email = email, Telephone = telephone };

        using var response = await client.PostAsJsonAsync(CustomersUrl, request, TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertValidationProblemAsync(response, invalidProperty);
        Assert.False(await factory.WithDbContextAsync(db =>
            db.Customers.AnyAsync(c => c.UserId == userId, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task CreateCustomer_ShouldStoreTrimmedValues_WhenFieldsArePaddedButValid()
    {
        var userId = CustomerApiFactory.NewUserId();
        using var client = factory.CreateCustomerClient(userId);
        var request = new CreateCustomerRequest("  Alan ", " Turing  ", " +44 161 000 ", " alan@example.com ", "  Wilmslow  ");

        using var response = await client.PostAsJsonAsync(CustomersUrl, request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = await factory.WithDbContextAsync(db =>
            db.Customers.AsNoTracking().SingleAsync(c => c.UserId == userId, TestContext.Current.CancellationToken));
        Assert.Equal(
            ("Alan", "Turing", "+44 161 000", "alan@example.com", "Wilmslow"),
            (stored.FirstName, stored.LastName, stored.Telephone, stored.Email, stored.Address));
    }

    [Fact]
    public async Task CreateCustomer_ShouldReturnForbiddenProblem_WhenTokenLacksWritePermission()
    {
        using var client = factory.CreateClientForUser(CustomerApiFactory.NewUserId(), CustomerApiFactory.ReadPermission);

        using var response = await client.PostAsJsonAsync(CustomersUrl, ValidRequest(), TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateCustomer_ShouldReturnUpdatedCustomer_WhenCallerOwnsProfile()
    {
        var userId = CustomerApiFactory.NewUserId();
        var created = await CreateProfileAsync(userId);
        using var client = factory.CreateCustomerClient(userId);
        var update = new UpdateCustomerRequest("Augusta", "King", "+44 20 0000 1111", "augusta@example.com", " Ockham Park ");

        using var response = await client.PutAsJsonAsync(
            $"{CustomersUrl}/{created.Id}", update, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expected = new CustomerResponse(
            created.Id, userId, "Augusta", "King", "+44 20 0000 1111", "augusta@example.com", "Ockham Park");
        Assert.Equal(expected, await response.Content.ReadFromJsonAsync<CustomerResponse>(TestContext.Current.CancellationToken));

        var stored = await factory.WithDbContextAsync(db =>
            db.Customers.AsNoTracking().SingleAsync(c => c.Id == created.Id, TestContext.Current.CancellationToken));
        Assert.Equal(("Augusta", "Ockham Park"), (stored.FirstName, stored.Address));
    }

    [Fact]
    public async Task UpdateCustomer_ShouldReturnNotFoundProblem_WhenProfileBelongsToAnotherUser()
    {
        // 404, not 403: a caller cannot even learn that someone else's profile exists.
        var created = await CreateProfileAsync(CustomerApiFactory.NewUserId());
        using var intruder = factory.CreateCustomerClient(CustomerApiFactory.NewUserId());
        var update = new UpdateCustomerRequest("Mallory", "Intruder", "555-0199", "mallory@example.com", "Elsewhere");

        using var response = await intruder.PutAsJsonAsync(
            $"{CustomersUrl}/{created.Id}", update, TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.NotFound);
        var stored = await factory.WithDbContextAsync(db =>
            db.Customers.AsNoTracking().SingleAsync(c => c.Id == created.Id, TestContext.Current.CancellationToken));
        Assert.Equal("Ada", stored.FirstName);
    }

    [Fact]
    public async Task UpdateCustomer_ShouldReturnForbiddenProblem_WhenTokenLacksWritePermission()
    {
        var userId = CustomerApiFactory.NewUserId();
        var created = await CreateProfileAsync(userId);
        using var client = factory.CreateClientForUser(userId, CustomerApiFactory.ReadPermission);
        var update = new UpdateCustomerRequest("Augusta", "King", "555-0100", "augusta@example.com", "Ockham Park");

        using var response = await client.PutAsJsonAsync(
            $"{CustomersUrl}/{created.Id}", update, TestContext.Current.CancellationToken);

        await ProblemAssertions.AssertProblemAsync(response, HttpStatusCode.Forbidden);
    }

    private static CreateCustomerRequest ValidRequest() =>
        new("Ada", "Lovelace", "+44 20 7946 0000", "ada@example.com", "12 St James's Square, London");

    /// <summary>Creates a profile through the API itself, on behalf of <paramref name="userId"/>.</summary>
    private async Task<CustomerResponse> CreateProfileAsync(int userId)
    {
        using var client = factory.CreateCustomerClient(userId);
        using var response = await client.PostAsJsonAsync(CustomersUrl, ValidRequest(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CustomerResponse>(TestContext.Current.CancellationToken))!;
    }
}
