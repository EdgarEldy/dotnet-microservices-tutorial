using Common.Lib.Dtos;
using Order.API.Dtos;

namespace Order.API.Services;

public interface IOrderService
{
    /// <summary>
    /// Creates an order once per <paramref name="idempotencyKey"/>: a retried request carrying the
    /// same key gets the order it already created back instead of a second one.
    /// </summary>
    Task<OrderResponse> CreateAsync(int userId, string idempotencyKey, CreateOrderRequest request, CancellationToken cancellationToken);

    /// <summary>The caller's own order (any order for an administrator), enriched through Refit.</summary>
    Task<OrderDetailsResponse> GetByIdAsync(int id, int userId, bool canReadAnyOrder, CancellationToken cancellationToken);

    /// <summary>The caller's own orders (all of them for an administrator), newest first.</summary>
    Task<PageResponse<OrderResponse>> GetPageAsync(PageRequest request, int userId, bool canReadAnyOrder, CancellationToken cancellationToken);

    /// <summary>Saga success: Pending to Confirmed. A no-op for any other status (redelivery).</summary>
    Task ConfirmAsync(int orderId, CancellationToken cancellationToken);

    /// <summary>Saga compensation: Pending to ConfirmationFailed. A no-op for any other status.</summary>
    Task MarkConfirmationFailedAsync(int orderId, string reason, CancellationToken cancellationToken);
}
