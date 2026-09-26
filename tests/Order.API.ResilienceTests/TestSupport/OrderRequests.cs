using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Order.API.ResilienceTests.TestSupport;

/// <summary>HTTP helpers shared by the resilience tests.</summary>
public static class OrderRequests
{
    public const string ProblemContentType = "application/problem+json";

    /// <summary>POST /api/v1/Orders with a fresh Idempotency-Key (a new order every call).</summary>
    public static async Task<HttpResponseMessage> PostOrderAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/Orders")
        {
            Content = JsonContent.Create(new
            {
                customerId = OrderApiFactory.CustomerId,
                productId = OrderApiFactory.ProductId,
                quantity = 2,
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>GET /api/v1/Orders/Diagnostics/Circuits, as a service name to state map.</summary>
    public static async Task<IReadOnlyDictionary<string, JsonElement>> GetCircuitsAsync(HttpClient adminClient)
    {
        using var response = await adminClient.GetAsync("/api/v1/Orders/Diagnostics/Circuits", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray()
            .ToDictionary(c => c.GetProperty("service").GetString()!, c => c.Clone());
    }

    /// <summary>The state of one circuit, as the diagnostics endpoint reports it.</summary>
    public static async Task<string> GetCircuitStateAsync(HttpClient adminClient, string service) =>
        (await GetCircuitsAsync(adminClient))[service].GetProperty("state").GetString()!;

    /// <summary>
    /// Asserts an RFC 9457 ProblemDetails response with the expected status and returns its detail.
    /// </summary>
    public static async Task<string> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(expectedStatus == response.StatusCode, $"Expected {expectedStatus}, got {response.StatusCode}: {body}");
        Assert.Equal(ProblemContentType, response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal((int)expectedStatus, root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));

        return root.TryGetProperty("detail", out var detail) ? detail.GetString() ?? string.Empty : string.Empty;
    }
}
