using System.Text.Json;
using Common.Lib.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Net.Http.Headers;
using Moq;

namespace Common.Lib.Tests.Exceptions;

public sealed class GlobalExceptionHandlerTests : IDisposable
{
    private const string ProblemJsonMediaType = "application/problem+json";

    private readonly ServiceProvider _services;
    private readonly FakeLogger<GlobalExceptionHandler> _logger = new();

    public GlobalExceptionHandlerTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task TryHandleAsync_ShouldReturnValidationProblemDetailsWith400_WhenValidationExceptionIsThrown()
    {
        var exception = new ValidationException(
        [
            new ValidationFailure("Name", "Name is required."),
            new ValidationFailure("Name", "Name must not exceed 100 characters."),
            new ValidationFailure("Price", "Price must be greater than 0."),
        ]);

        var (handled, context, body) = await HandleAsync(exception);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        AssertProblemJsonContentType(context);
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Equal("One or more validation errors occurred.", body.GetProperty("title").GetString());

        var errors = body.GetProperty("errors");
        Assert.Equal(2, errors.EnumerateObject().Count());
        Assert.Equal(
            ["Name is required.", "Name must not exceed 100 characters."],
            errors.GetProperty("Name").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(
            ["Price must be greater than 0."],
            errors.GetProperty("Price").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task TryHandleAsync_ShouldListEachMessageOnce_WhenSamePropertyHasDuplicateFailures()
    {
        var exception = new ValidationException(
        [
            new ValidationFailure("Email", "Email is invalid."),
            new ValidationFailure("Email", "Email is invalid."),
        ]);

        var (_, _, body) = await HandleAsync(exception);

        Assert.Equal(
            ["Email is invalid."],
            body.GetProperty("errors").GetProperty("Email").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task TryHandleAsync_ShouldReturn400WithEmptyErrors_WhenValidationExceptionHasNoFailures()
    {
        var exception = new ValidationException("Validation failed.");

        var (handled, context, body) = await HandleAsync(exception);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        AssertProblemJsonContentType(context);
        Assert.Empty(body.GetProperty("errors").EnumerateObject());
    }

    [Fact]
    public async Task TryHandleAsync_ShouldReturnProblemDetailsWith404_WhenResourceNotFoundExceptionIsThrown()
    {
        var exception = new ResourceNotFoundException("Product", 42);

        var (handled, context, body) = await HandleAsync(exception);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        AssertProblemJsonContentType(context);
        Assert.Equal(404, body.GetProperty("status").GetInt32());
        Assert.Equal("Resource not found", body.GetProperty("title").GetString());
        Assert.Equal("Product with id '42' was not found.", body.GetProperty("detail").GetString());
        Assert.False(body.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task TryHandleAsync_ShouldReturn404_WhenExceptionDerivesFromResourceNotFoundException()
    {
        var exception = new OrderNotFoundException();

        var (_, context, body) = await HandleAsync(exception);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("Order was not found.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task TryHandleAsync_ShouldReturnProblemDetailsWith422_WhenBusinessRuleExceptionIsThrown()
    {
        var exception = new BusinessRuleException("A product with this name already exists.");

        var (handled, context, body) = await HandleAsync(exception);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, context.Response.StatusCode);
        AssertProblemJsonContentType(context);
        Assert.Equal(422, body.GetProperty("status").GetInt32());
        Assert.Equal("Business rule violation", body.GetProperty("title").GetString());
        Assert.Equal("A product with this name already exists.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task TryHandleAsync_ShouldReturnProblemDetailsWith500WithoutExceptionMessage_WhenUnexpectedExceptionIsThrown()
    {
        const string secret = "Host=order-db;Password=s3cr3t";
        var exception = new InvalidOperationException(secret);

        var (handled, context, body) = await HandleAsync(exception);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        AssertProblemJsonContentType(context);
        Assert.Equal(500, body.GetProperty("status").GetInt32());
        Assert.Equal("An unexpected error occurred", body.GetProperty("title").GetString());

        var detail = body.GetProperty("detail").GetString();
        Assert.False(string.IsNullOrWhiteSpace(detail));
        Assert.DoesNotContain(secret, detail, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, body.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("validation")]
    [InlineData("not-found")]
    [InlineData("business-rule")]
    [InlineData("unexpected")]
    public async Task TryHandleAsync_ShouldIncludeTraceIdExtension_WhenAnyExceptionIsHandled(string kind)
    {
        var (_, context, body) = await HandleAsync(CreateException(kind));

        Assert.True(body.TryGetProperty("traceId", out var traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId.GetString()));
        Assert.Equal(context.Response.StatusCode, body.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task TryHandleAsync_ShouldLogErrorWithException_WhenUnexpectedExceptionIsThrown()
    {
        var exception = new InvalidOperationException("boom");

        await HandleAsync(exception);

        var record = Assert.Single(_logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Same(exception, record.Exception);
    }

    [Theory]
    [InlineData("validation")]
    [InlineData("not-found")]
    [InlineData("business-rule")]
    public async Task TryHandleAsync_ShouldLogWarning_WhenClientErrorExceptionIsThrown(string kind)
    {
        await HandleAsync(CreateException(kind));

        var record = Assert.Single(_logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, record.Level);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldReturnFalse_WhenProblemDetailsServiceCannotWrite()
    {
        var problemDetailsService = new Mock<IProblemDetailsService>();
        problemDetailsService
            .Setup(s => s.TryWriteAsync(It.IsAny<ProblemDetailsContext>()))
            .ReturnsAsync(false);
        var handler = new GlobalExceptionHandler(problemDetailsService.Object, _logger);
        var context = CreateHttpContext();

        var handled = await handler.TryHandleAsync(
            context, new BusinessRuleException("rule"), TestContext.Current.CancellationToken);

        Assert.False(handled);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldPassMappedProblemAndOriginalException_WhenWritingThroughProblemDetailsService()
    {
        ProblemDetailsContext? captured = null;
        var problemDetailsService = new Mock<IProblemDetailsService>();
        problemDetailsService
            .Setup(s => s.TryWriteAsync(It.IsAny<ProblemDetailsContext>()))
            .Callback<ProblemDetailsContext>(c => captured = c)
            .ReturnsAsync(true);
        var handler = new GlobalExceptionHandler(problemDetailsService.Object, _logger);
        var context = CreateHttpContext();
        var exception = new ResourceNotFoundException("Customer", 7);

        await handler.TryHandleAsync(context, exception, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Same(context, captured.HttpContext);
        Assert.Same(exception, captured.Exception);
        Assert.Equal(StatusCodes.Status404NotFound, captured.ProblemDetails.Status);
    }

    private static Exception CreateException(string kind) => kind switch
    {
        "validation" => new ValidationException([new ValidationFailure("Name", "Name is required.")]),
        "not-found" => new ResourceNotFoundException("Product", 1),
        "business-rule" => new BusinessRuleException("rule violated"),
        "unexpected" => new InvalidOperationException("boom"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext { RequestServices = _services };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/v1/Catalog/Products/42";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private async Task<(bool Handled, DefaultHttpContext Context, JsonElement Body)> HandleAsync(Exception exception)
    {
        var handler = new GlobalExceptionHandler(
            _services.GetRequiredService<IProblemDetailsService>(), _logger);
        var context = CreateHttpContext();

        var handled = await handler.TryHandleAsync(context, exception, TestContext.Current.CancellationToken);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(
            context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        return (handled, context, document.RootElement.Clone());
    }

    private static void AssertProblemJsonContentType(HttpContext context)
    {
        Assert.NotNull(context.Response.ContentType);
        var mediaType = MediaTypeHeaderValue.Parse(context.Response.ContentType);
        Assert.Equal(ProblemJsonMediaType, mediaType.MediaType.Value);
    }

    private sealed class OrderNotFoundException() : ResourceNotFoundException("Order was not found.");
}
