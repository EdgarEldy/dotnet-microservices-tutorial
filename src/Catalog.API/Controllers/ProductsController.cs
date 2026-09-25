using Catalog.API.Dtos;
using Catalog.API.Security;
using Catalog.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Catalog.API.Controllers;

/// <summary>
/// /api/v1/Catalog/Products: translates HTTP to service calls, nothing else. Same routing choice
/// as CategoriesController. GET {id} is also what order-api calls through Refit.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/Catalog/[controller]")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
public sealed class ProductsController(IProductService productService) : ControllerBase
{
    /// <summary>
    /// One page of products, optionally filtered by categoryId; the total and the page links are
    /// in the response headers.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ProductResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<ProductResponse>>> GetProducts(
        [FromQuery] ProductPageRequest request,
        CancellationToken cancellationToken)
    {
        var page = await productService.GetPageAsync(request, cancellationToken);
        page.WriteHeaders(HttpContext);
        return Ok(page.Items);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ProductResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductResponse>> GetProduct(int id, CancellationToken cancellationToken) =>
        await productService.GetByIdAsync(id, cancellationToken);

    [HttpPost]
    [Authorize(Policy = CatalogPermissions.Write)]
    [ProducesResponseType(typeof(ProductResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ProductResponse>> CreateProduct(
        CreateProductRequest request,
        CancellationToken cancellationToken)
    {
        var product = await productService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetProduct), new { id = product.Id }, product);
    }
}
