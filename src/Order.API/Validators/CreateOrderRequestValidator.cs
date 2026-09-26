using FluentValidation;
using Order.API.Data;
using Order.API.Dtos;

namespace Order.API.Validators;

/// <summary>
/// Shape only. Whether the product and the customer exist is checked by OrderService against
/// catalog-api and customer-api (a business rule, 422), not here.
/// </summary>
public sealed class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    public CreateOrderRequestValidator()
    {
        RuleFor(r => r.CustomerId).GreaterThan(0);
        RuleFor(r => r.ProductId).GreaterThan(0);
        RuleFor(r => r.Quantity).InclusiveBetween(1, OrderLimits.MaxQuantity);
    }
}
