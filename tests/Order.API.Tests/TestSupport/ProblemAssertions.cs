using System.Net;
using System.Text.Json;

namespace Order.API.Tests.TestSupport;

public static class ProblemAssertions
{
    public const string ProblemContentType = "application/problem+json";

    /// <summary>
    /// Asserts an RFC 9457 ProblemDetails response (content type, status, title) and returns its
    /// JSON root for further checks.
    /// </summary>
    public static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(ProblemContentType, response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement.Clone();

        Assert.Equal((int)expectedStatus, root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));

        return root;
    }

    /// <summary>A 400 ValidationProblemDetails whose errors contain the given property.</summary>
    public static async Task AssertValidationProblemAsync(HttpResponseMessage response, string propertyName)
    {
        var root = await AssertProblemAsync(response, HttpStatusCode.BadRequest);

        var errors = root.GetProperty("errors");
        Assert.True(
            errors.TryGetProperty(propertyName, out var messages),
            $"Expected a validation error for '{propertyName}', got: {errors}");
        Assert.NotEqual(0, messages.GetArrayLength());
    }
}
