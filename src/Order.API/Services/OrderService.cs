using System.Net;
using Common.Lib.Dtos;
using Common.Lib.Exceptions;
using Contracts;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Order.API.Clients;
using Order.API.Data;
using Order.API.Dtos;
using Order.API.Models;
using Refit;

namespace Order.API.Services;

/// <summary>
/// The only place allowed to touch orders, the Refit clients and the publish endpoint.
/// </summary>
public sealed class OrderService(
    AppDbContext dbContext,
    IProductClient productClient,
    ICustomerClient customerClient,
    IPublishEndpoint publishEndpoint,
    TimeProvider timeProvider,
    ILogger<OrderService> logger) : IOrderService
{
    public async Task<OrderResponse> CreateAsync(
        int userId,
        string idempotencyKey,
        CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        // 1. Idempotency first: a retry (client, or Refit/HttpClient retrying a timeout) with the
        //    same key gets the order it already created, before any other work is done.
        if (await FindByIdempotencyKeyAsync(idempotencyKey, userId, cancellationToken) is { } existing)
        {
            logger.LogInformation("Idempotent replay: order {OrderId} returned for its key", existing.Id);
            return existing;
        }

        // 2. Synchronous validation against the owning services (Refit, the caller's token forwarded).
        var product = await GetProductAsync(request.ProductId, cancellationToken);
        var customer = await GetCustomerAsync(request.CustomerId, cancellationToken);

        // 3. One local transaction: the order (Pending), its idempotency key and the OrderCreatedEvent
        //    outbox row commit together, or not at all. Npgsql's retrying execution strategy (enabled
        //    by Aspire) requires a user transaction to run inside it.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async ct =>
            {
                ForgetPreviousAttempt();

                await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

                var order = new Models.Order
                {
                    UserId = userId,
                    CustomerId = customer.Id,
                    ProductId = product.Id,
                    Quantity = request.Quantity,
                    Total = product.UnitPrice * request.Quantity,
                    Status = OrderStatus.Pending,
                };
                dbContext.Orders.Add(order);
                dbContext.IdempotencyKeys.Add(new IdempotencyKey
                {
                    Key = idempotencyKey,
                    Order = order,
                    CreatedAt = timeProvider.GetUtcNow(),
                });

                // First save: the order gets its id, still inside the uncommitted transaction.
                await dbContext.SaveChangesAsync(ct);

                // Publish BEFORE the final SaveChanges: the EF Core outbox writes the event to
                // OutboxMessage through this DbContext, so it commits (or rolls back) with the order.
                // MassTransit relays it to Kafka only after the commit, never before.
                await publishEndpoint.Publish(
                    new OrderCreatedEvent(
                        order.Id,
                        customer.Id,
                        customer.Email,
                        $"{customer.FirstName} {customer.LastName}",
                        product.Id,
                        product.ProductName,
                        order.Quantity,
                        order.Total),
                    ct);

                await dbContext.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                logger.LogInformation("Order {OrderId} created as Pending", order.Id);
                return ToResponse(order);
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        })
        {
            // A concurrent request with the same key committed between the check and the insert:
            // this transaction rolled back, answer with the order the other one created.
            dbContext.ChangeTracker.Clear();
            return await FindByIdempotencyKeyAsync(idempotencyKey, userId, cancellationToken)
                ?? throw new BusinessRuleException("The order for this Idempotency-Key could not be read back.", exception);
        }
    }

    public async Task<OrderDetailsResponse> GetByIdAsync(int id, int userId, bool canReadAnyOrder, CancellationToken cancellationToken)
    {
        var order = await ScopeToCaller(dbContext.Orders.AsNoTracking(), userId, canReadAnyOrder)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new ResourceNotFoundException(nameof(Models.Order), id);

        // API Composition: product and customer come from their owning services. A part that
        // cannot be read (service down, profile not visible to this caller) is left null rather
        // than failing the whole order.
        var product = await TryGetAsync(() => productClient.GetProductAsync(order.ProductId, cancellationToken), "catalog-api");
        var customer = await TryGetAsync(() => customerClient.GetCustomerAsync(order.CustomerId, cancellationToken), "customer-api");

        return new OrderDetailsResponse(
            order.Id, order.CustomerId, order.ProductId, order.Quantity, order.Total, order.Status.ToString(), product, customer);
    }

    public Task<PageResponse<OrderResponse>> GetPageAsync(PageRequest request, int userId, bool canReadAnyOrder, CancellationToken cancellationToken) =>
        ScopeToCaller(dbContext.Orders.AsNoTracking(), userId, canReadAnyOrder)
            .OrderByDescending(o => o.Id)
            .Select(o => new OrderResponse(o.Id, o.CustomerId, o.ProductId, o.Quantity, o.Total, o.Status.ToString()))
            .ToPageAsync(request, cancellationToken);

    public Task ConfirmAsync(int orderId, CancellationToken cancellationToken) =>
        TransitionFromPendingAsync(orderId, OrderStatus.Confirmed, reason: null, cancellationToken);

    public Task MarkConfirmationFailedAsync(int orderId, string reason, CancellationToken cancellationToken) =>
        TransitionFromPendingAsync(orderId, OrderStatus.ConfirmationFailed, reason, cancellationToken);

    /// <summary>
    /// One conditional UPDATE, so the transition is atomic and idempotent: Kafka delivers at least
    /// once, and a redelivered or late outcome event finds the order no longer Pending and changes
    /// nothing. Pending is the only state an outcome may leave.
    /// </summary>
    private async Task TransitionFromPendingAsync(int orderId, OrderStatus target, string? reason, CancellationToken cancellationToken)
    {
        var updated = await dbContext.Orders
            .Where(o => o.Id == orderId && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(setters => setters.SetProperty(o => o.Status, target), cancellationToken);

        if (updated == 1)
        {
            logger.LogInformation("Order {OrderId} moved to {Status} {Reason}", orderId, target, reason);
        }
        else
        {
            logger.LogInformation("Order {OrderId} not Pending (or unknown): {Status} ignored", orderId, target);
        }
    }

    private async Task<OrderResponse?> FindByIdempotencyKeyAsync(string idempotencyKey, int userId, CancellationToken cancellationToken)
    {
        var order = await dbContext.IdempotencyKeys
            .AsNoTracking()
            .Where(k => k.Key == idempotencyKey)
            .Select(k => k.Order)
            .FirstOrDefaultAsync(cancellationToken);

        if (order is null)
        {
            return null;
        }

        // Keys are unique across the whole table: someone else's key is a client error, never a
        // way to read another user's order.
        if (order.UserId != userId)
        {
            throw new BusinessRuleException("This Idempotency-Key was already used for another request.");
        }

        return ToResponse(order);
    }

    private async Task<ProductDto> GetProductAsync(int productId, CancellationToken cancellationToken)
    {
        try
        {
            return await productClient.GetProductAsync(productId, cancellationToken);
        }
        catch (ApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new BusinessRuleException($"Product with id '{productId}' does not exist.", exception);
        }
        catch (ApiException exception)
        {
            // Never leak a raw HTTP exception: the caller gets a clear business error.
            throw new BusinessRuleException(
                $"Product with id '{productId}' could not be validated (catalog-api answered {(int)exception.StatusCode}).", exception);
        }
        catch (Exception exception) when (IsUnreachable(exception))
        {
            throw new BusinessRuleException(
                $"Product with id '{productId}' could not be validated: catalog-api is unreachable.", exception);
        }
    }

    private async Task<CustomerDto> GetCustomerAsync(int customerId, CancellationToken cancellationToken)
    {
        try
        {
            return await customerClient.GetCustomerAsync(customerId, cancellationToken);
        }
        catch (ApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // customer-api answers 404 both for a missing profile and for someone else's.
            throw new BusinessRuleException($"Customer with id '{customerId}' does not exist.", exception);
        }
        catch (ApiException exception)
        {
            throw new BusinessRuleException(
                $"Customer with id '{customerId}' could not be validated (customer-api answered {(int)exception.StatusCode}).", exception);
        }
        catch (Exception exception) when (IsUnreachable(exception))
        {
            throw new BusinessRuleException(
                $"Customer with id '{customerId}' could not be validated: customer-api is unreachable.", exception);
        }
    }

    /// <summary>
    /// The call never got an HTTP answer: Refit wraps network errors and resilience-pipeline
    /// rejections (timeout, open circuit) in ApiRequestException, and they can also surface raw.
    /// </summary>
    private static bool IsUnreachable(Exception exception) =>
        exception is ApiRequestException or HttpRequestException or TimeoutException or Polly.ExecutionRejectedException;

    private async Task<T?> TryGetAsync<T>(Func<Task<T>> call, string serviceName)
        where T : class
    {
        try
        {
            return await call();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Any failure of the downstream call: an HTTP error (ApiException), a network error, or
            // the resilience pipeline refusing the call (open circuit, timeout). A request aborted by
            // the client (OperationCanceledException) still propagates.
            logger.LogWarning(exception, "Order details: {Service} could not provide its part", serviceName);
            return null;
        }
    }

    private static IQueryable<Models.Order> ScopeToCaller(IQueryable<Models.Order> orders, int userId, bool canReadAnyOrder) =>
        canReadAnyOrder ? orders : orders.Where(o => o.UserId == userId);

    private static OrderResponse ToResponse(Models.Order order) =>
        new(order.Id, order.CustomerId, order.ProductId, order.Quantity, order.Total, order.Status.ToString());

    /// <summary>
    /// A retried execution-strategy attempt starts from a clean change tracker, except MassTransit's
    /// OutboxState, which the bus outbox keeps tracked for the whole scope.
    /// </summary>
    private void ForgetPreviousAttempt()
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is not OutboxState)
            {
                entry.State = EntityState.Detached;
            }
        }
    }
}
