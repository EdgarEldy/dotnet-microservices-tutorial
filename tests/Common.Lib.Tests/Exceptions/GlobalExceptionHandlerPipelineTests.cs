using System.Net;
using System.Text.Json;
using Common.Lib.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Common.Lib.Tests.Exceptions;

/// <summary>
/// Proves the real middleware wiring every service uses (AddProblemDetails,
/// AddExceptionHandler&lt;GlobalExceptionHandler&gt;, UseExceptionHandler) produces ProblemDetails.
/// </summary>
public sealed class GlobalExceptionHandlerPipelineTests(GlobalExceptionHandlerPipelineTests.PipelineFixture fixture)
    : IClassFixture<GlobalExceptionHandlerPipelineTests.PipelineFixture>
{
    [Theory]
    [InlineData("/throw/validation", HttpStatusCode.BadRequest)]
    [InlineData("/throw/not-found", HttpStatusCode.NotFound)]
    [InlineData("/throw/business-rule", HttpStatusCode.UnprocessableEntity)]
    [InlineData("/throw/bad-request", HttpStatusCode.RequestEntityTooLarge)]
    [InlineData("/throw/unexpected", HttpStatusCode.InternalServerError)]
    public async Task UseExceptionHandler_ShouldReturnProblemDetails_WhenEndpointThrows(string path, HttpStatusCode expectedStatus)
    {
        using var response = await fixture.Client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await ReadJsonAsync(response);
        Assert.Equal((int)expectedStatus, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task UseExceptionHandler_ShouldReturnErrorsByProperty_WhenEndpointThrowsValidationException()
    {
        using var response = await fixture.Client.GetAsync("/throw/validation", TestContext.Current.CancellationToken);

        var body = await ReadJsonAsync(response);
        Assert.Equal(
            ["Name is required."],
            body.GetProperty("errors").GetProperty("Name").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task UseExceptionHandler_ShouldNotLeakExceptionMessage_WhenEndpointThrowsUnexpectedException()
    {
        using var response = await fixture.Client.GetAsync("/throw/unexpected", TestContext.Current.CancellationToken);

        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(PipelineFixture.SecretMessage, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UseExceptionHandler_ShouldReturnItsStatusAndMessage_WhenEndpointThrowsBadHttpRequestException()
    {
        using var response = await fixture.Client.GetAsync("/throw/bad-request", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(
            ReasonPhrases.GetReasonPhrase(StatusCodes.Status413PayloadTooLarge),
            body.GetProperty("title").GetString());
        Assert.Equal(PipelineFixture.BadRequestMessage, body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task UseExceptionHandler_ShouldLeaveSuccessResponseUntouched_WhenEndpointDoesNotThrow()
    {
        using var response = await fixture.Client.GetAsync("/ok", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await ReadJsonAsync(response);
        Assert.Equal(1, body.GetProperty("id").GetInt32());
        Assert.False(body.TryGetProperty("status", out _));
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        return document.RootElement.Clone();
    }

    public sealed class PipelineFixture : IAsyncLifetime
    {
        public const string SecretMessage = "Host=order-db;Password=s3cr3t";

        public const string BadRequestMessage = "Request body too large.";

        private WebApplication? _app;

        public HttpClient Client { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddProblemDetails();
            builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

            _app = builder.Build();
            _app.UseExceptionHandler();

            _app.MapGet("/ok", () => TypedResults.Ok(new { Id = 1, Name = "Keyboard" }));
            _app.MapGet("/throw/validation", void () =>
                throw new ValidationException([new ValidationFailure("Name", "Name is required.")]));
            _app.MapGet("/throw/not-found", void () => throw new ResourceNotFoundException("Product", 42));
            _app.MapGet("/throw/business-rule", void () => throw new BusinessRuleException("Duplicate name."));
            _app.MapGet("/throw/bad-request", void () =>
                throw new BadHttpRequestException(BadRequestMessage, StatusCodes.Status413PayloadTooLarge));
            _app.MapGet("/throw/unexpected", void () => throw new InvalidOperationException(SecretMessage));

            await _app.StartAsync();
            Client = _app.GetTestClient();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }
        }
    }
}
