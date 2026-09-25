using Catalog.API.Dtos;
using Catalog.API.Security;
using Catalog.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Catalog.API.Controllers;

/// <summary>
/// /api/v1/Catalog/Categories: translates HTTP to service calls, nothing else. "Catalog" is the
/// README's fixed prefix; the last segment comes from the [controller] token, so no route casing
/// is typed by hand. Input is validated by the global ValidationFilter; errors are thrown by the
/// services and become ProblemDetails.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/Catalog/[controller]")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
public sealed class CategoriesController(ICategoryService categoryService) : ControllerBase
{
    /// <summary>One page of categories; the total and the page links are in the response headers.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<CategoryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<CategoryResponse>>> GetCategories(
        [FromQuery] PageRequest request,
        CancellationToken cancellationToken)
    {
        var page = await categoryService.GetPageAsync(request, cancellationToken);
        page.WriteHeaders(HttpContext);
        return Ok(page.Items);
    }

    /// <summary>
    /// 201 with the created category. The README defines no GET-by-id for categories, so the
    /// response carries no Location header.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CatalogPermissions.Write)]
    [ProducesResponseType(typeof(CategoryResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CategoryResponse>> CreateCategory(
        CreateCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var category = await categoryService.CreateAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, category);
    }
}
