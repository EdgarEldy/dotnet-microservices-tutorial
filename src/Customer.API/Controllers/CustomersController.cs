using Customer.API.Dtos;
using Customer.API.Security;
using Customer.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Customer.API.Controllers;

/// <summary>
/// /api/v1/Customers: translates HTTP to service calls, nothing else. GET {id} is also what
/// order-api calls through Refit.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/[controller]")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
public sealed class CustomersController(ICustomerService customerService) : ControllerBase
{
    [HttpGet("{id:int}")]
    [Authorize(Policy = CustomerPermissions.Read)]
    [ProducesResponseType(typeof(CustomerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CustomerResponse>> GetCustomer(int id, CancellationToken cancellationToken) =>
        await customerService.GetByIdAsync(id, cancellationToken);

    /// <summary>Creates the caller's own profile; the UserId comes from the access token.</summary>
    [HttpPost]
    [Authorize(Policy = CustomerPermissions.Write)]
    [ProducesResponseType(typeof(CustomerResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CustomerResponse>> CreateCustomer(
        CreateCustomerRequest request,
        CancellationToken cancellationToken)
    {
        var customer = await customerService.CreateAsync(User.GetUserId(), request, cancellationToken);
        return CreatedAtAction(nameof(GetCustomer), new { id = customer.Id }, customer);
    }

    /// <summary>Updates a profile the caller owns (anyone else's answers 404).</summary>
    [HttpPut("{id:int}")]
    [Authorize(Policy = CustomerPermissions.Write)]
    [ProducesResponseType(typeof(CustomerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CustomerResponse>> UpdateCustomer(
        int id,
        UpdateCustomerRequest request,
        CancellationToken cancellationToken) =>
        await customerService.UpdateAsync(id, User.GetUserId(), request, cancellationToken);
}
