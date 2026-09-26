using Customer.API.Data;
using Customer.API.Dtos;
using FluentValidation;

namespace Customer.API.Validators;

public sealed class CreateCustomerRequestValidator : AbstractValidator<CreateCustomerRequest>
{
    public CreateCustomerRequestValidator()
    {
        RuleFor(r => r.FirstName).TrimmedText(CustomerLimits.NameMaxLength);
        RuleFor(r => r.LastName).TrimmedText(CustomerLimits.NameMaxLength);
        RuleFor(r => r.Telephone).Telephone();
        RuleFor(r => r.Email).EmailAddressValue();
        RuleFor(r => r.Address).TrimmedText(CustomerLimits.AddressMaxLength);
    }
}
