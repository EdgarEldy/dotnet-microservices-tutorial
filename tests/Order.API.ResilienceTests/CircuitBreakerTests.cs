using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Order.API.ResilienceTests.TestSupport;

namespace Order.API.ResilienceTests;

/// <summary>
/// The circuit breaker, fallback and timeouts around order-api's Refit clients, driven through the
/// real HTTP pipeline: POST /api/v1/Orders against WireMock stand-ins for catalog-api and customer-api.
/// Test settings: the circuit opens once at least 4 attempts in 30 s failed half of the time, one
/// retry per call (so two attempts per order), generous timeouts (10 s per attempt, 30 s in total)
/// except in the tests about timeouts. Proofs rest on WireMock's request counts and the fallback's
/// reason ("circuit open", "timed out"); the time budgets only guard against a hang, so they are
/// wide enough for a loaded machine.
/// </summary>
public sealed class CircuitBreakerTests(ContainersFixture containers)
{
    private const int MinimumThroughput = 4;
    private const string CatalogApi = "catalog-api";
    private const string CustomerApi = "customer-api";

    [Fact]
    public async Task CreateOrder_ShouldOpenCircuitAndFailFastWithoutCallingCatalog_WhenCatalogKeepsFailing()
    {
        await using var factory = await OrderApiFactory.CreateAsync(containers);
        factory.CatalogAnswers(500);
        factory.CustomersReturnCustomer();
        using var client = factory.CreateCustomerClient();
        using var admin = factory.CreateAdminClient();

        // Two attempts per order (first try + one retry): the second order reaches the threshold.
        using (var first = await OrderRequests.PostOrderAsync(client))
        {
            var detail = await OrderRequests.AssertProblemAsync(first, HttpStatusCode.ServiceUnavailable);
            Assert.Contains("product service is unavailable", detail, StringComparison.Ordinal);
        }

        Assert.Equal("Closed", await OrderRequests.GetCircuitStateAsync(admin, CatalogApi));

        using (var second = await OrderRequests.PostOrderAsync(client))
        {
            await OrderRequests.AssertProblemAsync(second, HttpStatusCode.ServiceUnavailable);
        }

        Assert.Equal(MinimumThroughput, await factory.WaitForCatalogRequestsAsync(MinimumThroughput));
        Assert.Equal("Open", await OrderRequests.GetCircuitStateAsync(admin, CatalogApi));

        // Open circuit: the fallback answers at once, catalog-api is not called at all.
        var stopwatch = Stopwatch.StartNew();
        using var third = await OrderRequests.PostOrderAsync(client);
        stopwatch.Stop();

        var fastDetail = await OrderRequests.AssertProblemAsync(third, HttpStatusCode.ServiceUnavailable);
        Assert.Contains("product service is unavailable (circuit open)", fastDetail, StringComparison.Ordinal);
        // Waiting for one more request than expected proves none came (the wait runs its full bound).
        Assert.Equal(MinimumThroughput, await factory.WaitForCatalogRequestsAsync(MinimumThroughput + 1));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"The open circuit took {stopwatch.Elapsed} to answer.");

        var circuits = await OrderRequests.GetCircuitsAsync(admin);
        Assert.Equal(TimeSpan.FromSeconds(30), TimeSpan.Parse(circuits[CatalogApi].GetProperty("breakDuration").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("Closed", circuits[CustomerApi].GetProperty("state").GetString());

        var opened = Assert.Single(factory.Logs.GetSnapshot(), r => r.Message.StartsWith("Circuit breaker for catalog-api opened", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, opened.Level);
        Assert.Equal(CatalogApi, opened.GetStructuredStateValue("Service"));
    }

    [Fact]
    public async Task CreateOrder_ShouldOpenCircuitAndReturn503_WhenCatalogIsStopped()
    {
        await using var factory = await OrderApiFactory.CreateAsync(containers, new Dictionary<string, string?>
        {
            ["AttemptTimeout"] = "00:00:01",
        });
        factory.CustomersReturnCustomer();
        using var client = factory.CreateCustomerClient();
        using var admin = factory.CreateAdminClient();

        // The README's induced failure: catalog-api is down (connection refused, or no answer).
        factory.Catalog.Stop();

        for (var order = 0; order < MinimumThroughput / 2; order++)
        {
            using var response = await OrderRequests.PostOrderAsync(client);
            var detail = await OrderRequests.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
            Assert.Contains("product service is unavailable", detail, StringComparison.Ordinal);
        }

        Assert.Equal("Open", await OrderRequests.GetCircuitStateAsync(admin, CatalogApi));

        var stopwatch = Stopwatch.StartNew();
        using var fast = await OrderRequests.PostOrderAsync(client);
        stopwatch.Stop();

        var fastDetail = await OrderRequests.AssertProblemAsync(fast, HttpStatusCode.ServiceUnavailable);
        // "(circuit open)" proves no call was attempted; the time bound only guards against a hang.
        Assert.Contains("(circuit open)", fastDetail, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"The open circuit took {stopwatch.Elapsed} to answer.");
    }

    [Fact]
    public async Task CreateOrder_ShouldHalfOpenThenCloseCircuit_WhenCatalogRecoversAfterBreakDuration()
    {
        var breakDuration = TimeSpan.FromSeconds(1);
        await using var factory = await OrderApiFactory.CreateAsync(containers, new Dictionary<string, string?>
        {
            ["BreakDuration"] = "00:00:01",
        });
        factory.CatalogAnswers(503);
        factory.CustomersReturnCustomer();
        using var client = factory.CreateCustomerClient();
        using var admin = factory.CreateAdminClient();

        for (var order = 0; order < MinimumThroughput / 2; order++)
        {
            using var failed = await OrderRequests.PostOrderAsync(client);
            await OrderRequests.AssertProblemAsync(failed, HttpStatusCode.ServiceUnavailable);
        }

        Assert.Equal("Open", await OrderRequests.GetCircuitStateAsync(admin, CatalogApi));

        // catalog-api is back; once the break duration is over, the next call is the trial call.
        factory.Catalog.ResetMappings();
        factory.CatalogReturnsProduct();
        await Task.Delay(breakDuration + TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        using var recovered = await OrderRequests.PostOrderAsync(client);

        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        Assert.Equal("Closed", await OrderRequests.GetCircuitStateAsync(admin, CatalogApi));

        var messages = factory.Logs.GetSnapshot()
            .Where(r => r.Message.StartsWith("Circuit breaker for catalog-api", StringComparison.Ordinal))
            .Select(r => r.Message)
            .ToList();
        Assert.Collection(
            messages,
            m => Assert.Contains("opened", m, StringComparison.Ordinal),
            m => Assert.Contains("half-opened", m, StringComparison.Ordinal),
            m => Assert.Contains("closed", m, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateOrder_ShouldReturn503WithoutHanging_WhenCatalogTimesOut()
    {
        await using var factory = await OrderApiFactory.CreateAsync(containers, new Dictionary<string, string?>
        {
            ["AttemptTimeout"] = "00:00:00.500",
            ["TotalTimeout"] = "00:00:03",
        });
        factory.Catalog.Given(WireMock.RequestBuilders.Request.Create().WithPath(OrderApiFactory.ProductPath).UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(10)));
        factory.CustomersReturnCustomer();
        using var client = factory.CreateCustomerClient();

        var stopwatch = Stopwatch.StartNew();
        using var response = await OrderRequests.PostOrderAsync(client);
        stopwatch.Stop();

        var detail = await OrderRequests.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
        Assert.Contains("product service is unavailable (timed out)", detail, StringComparison.Ordinal);
        // Well under WireMock's 10 s delay: the 3 s total timeout ended the call, not the answer.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8), $"The timed out call took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task CreateOrder_ShouldReturn422AndKeepCircuitClosed_WhenProductDoesNotExist()
    {
        await using var factory = await OrderApiFactory.CreateAsync(containers);
        factory.CatalogAnswers(404);
        factory.CustomersReturnCustomer();
        using var client = factory.CreateCustomerClient();
        using var admin = factory.CreateAdminClient();

        // Twice the threshold: a 404 is an answer, never a failure of catalog-api.
        for (var order = 0; order < MinimumThroughput * 2; order++)
        {
            using var response = await OrderRequests.PostOrderAsync(client);
            var detail = await OrderRequests.AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
            Assert.Contains($"Product with id '{OrderApiFactory.ProductId}' does not exist", detail, StringComparison.Ordinal);
        }

        // Not retried either: one request per order.
        Assert.Equal(MinimumThroughput * 2, await factory.WaitForCatalogRequestsAsync(MinimumThroughput * 2));

        var catalog = (await OrderRequests.GetCircuitsAsync(admin))[CatalogApi];
        Assert.Equal("Closed", catalog.GetProperty("state").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, catalog.GetProperty("lastChangedAt").ValueKind);
    }

    [Fact]
    public async Task CreateOrder_ShouldReturn503_WhenCustomerServiceIsUnavailable()
    {
        await using var factory = await OrderApiFactory.CreateAsync(containers);
        factory.CatalogReturnsProduct();
        factory.Customers.Given(WireMock.RequestBuilders.Request.Create().WithPath(OrderApiFactory.CustomerPath).UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(502));
        using var client = factory.CreateCustomerClient();

        using var response = await OrderRequests.PostOrderAsync(client);

        var detail = await OrderRequests.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
        // Deterministic again since the timeouts are generous and WireMock is warmed up: the 502 was
        // retried once, then the fallback reported it.
        Assert.Contains("customer service is unavailable (answered 502 after the retries)", detail, StringComparison.Ordinal);
        Assert.Equal(2, await factory.WaitForCustomerRequestsAsync(2));
    }

    [Fact]
    public async Task GetCircuits_ShouldReturn403_WhenCallerIsNotAdmin()
    {
        await using var factory = await OrderApiFactory.CreateAsync(containers);
        using var client = factory.CreateCustomerClient();

        using var response = await client.GetAsync("/api/v1/Orders/Diagnostics/Circuits", TestContext.Current.CancellationToken);

        await OrderRequests.AssertProblemAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetCircuits_ShouldReportBothCircuitsClosed_WhenNoCallFailed()
    {
        await using var factory = await OrderApiFactory.CreateAsync(containers);
        using var admin = factory.CreateAdminClient();

        var circuits = await OrderRequests.GetCircuitsAsync(admin);

        Assert.Equal([CatalogApi, CustomerApi], circuits.Keys.Order(StringComparer.Ordinal));
        Assert.All(circuits.Values, c => Assert.Equal("Closed", c.GetProperty("state").GetString()));
    }

    [Theory]
    [InlineData("RetryCount", "0")]
    [InlineData("MinimumThroughput", "1")]
    [InlineData("FailureRatio", "1.5")]
    [InlineData("BreakDuration", "00:00:00.100")]
    [InlineData("TotalTimeout", "00:00:01")]
    public async Task Startup_ShouldFail_WhenResilienceOptionIsInvalid(string key, string value)
    {
        await using var factory = await OrderApiFactory.CreateAsync(containers, new Dictionary<string, string?> { [key] = value });

        var exception = Assert.ThrowsAny<Exception>(() => factory.Server);

        Assert.Contains($"Resilience:Downstream:{key}", exception.ToString(), StringComparison.Ordinal);
    }
}
