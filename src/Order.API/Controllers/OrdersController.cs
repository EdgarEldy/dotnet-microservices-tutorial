using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Order.API.Data;
using Order.API.Dtos;
using Order.API.Security;
using Order.API.Services;

namespace Order.API.Controllers;

/// <summary>
/// /api/v1/Orders: translates HTTP to service calls, nothing else. Callers see their own orders;
/// the Admin role sees every order.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/[controller]")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
public sealed class OrdersController(IOrderService orderService) : ControllerBase
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    [HttpGet("{id:int}")]
    [Authorize(Policy = OrderPermissions.Read)]
    [ProducesResponseType(typeof(OrderDetailsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrderDetailsResponse>> GetOrder(int id, CancellationToken cancellationToken) =>
        await orderService.GetByIdAsync(id, User.GetUserId(), User.IsInRole(OrderPermissions.AdminRole), cancellationToken);

    /// <summary>One page of orders, newest first; the total and the page links are in the headers.</summary>
    [HttpGet]
    [Authorize(Policy = OrderPermissions.Read)]
    [ProducesResponseType(typeof(IReadOnlyList<OrderResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<OrderResponse>>> GetOrders(
        [FromQuery] PageRequest request,
        CancellationToken cancellationToken)
    {
        var page = await orderService.GetPageAsync(
            request, User.GetUserId(), User.IsInRole(OrderPermissions.AdminRole), cancellationToken);
        page.WriteHeaders(HttpContext);
        return Ok(page.Items);
    }

    /// <summary>
    /// Creates an order, exactly once per Idempotency-Key header: a retry with the same key returns
    /// the same order. The order starts Pending; the Saga confirms it asynchronously.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = OrderPermissions.Write)]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OrderResponse>> CreateOrder(
        CreateOrderRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = idempotencyKey?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > OrderLimits.IdempotencyKeyMaxLength)
        {
            throw new ValidationException(
            [
                new ValidationFailure(
                    IdempotencyKeyHeader,
                    $"The {IdempotencyKeyHeader} header is required, at most {OrderLimits.IdempotencyKeyMaxLength} characters."),
            ]);
        }

        var order = await orderService.CreateAsync(User.GetUserId(), key, request, cancellationToken);
        return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, order);
    }

    /// <summary>The current state of the circuit breakers guarding catalog-api and customer-api.</summary>
    [HttpGet("Diagnostics/Circuits")]
    [Authorize(Roles = OrderPermissions.AdminRole)]
    [ProducesResponseType(typeof(IReadOnlyList<CircuitStateResponse>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<CircuitStateResponse>> GetCircuits([FromServices] ICircuitBreakerMonitor circuitBreakerMonitor) =>
        Ok(circuitBreakerMonitor.GetCircuits());
}
