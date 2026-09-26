namespace Order.API.Models;

/// <summary>
/// An order for one product. CustomerId and ProductId reference customer-api's and catalog-api's
/// entities as plain values: no foreign key crosses a service boundary. UserId (the identity-api
/// account that placed the order, from the access token) scopes who may read it.
/// </summary>
public class Order
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public int CustomerId { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    /// <summary>UnitPrice read from catalog-api at creation time, times Quantity.</summary>
    public decimal Total { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Pending;
}
