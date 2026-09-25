using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.WebUtilities;

namespace Common.Lib.Dtos;

/// <summary>
/// One page of a paginated query, plus the metadata needed to describe it.
/// The body of a paginated endpoint is only <see cref="Items"/>; the metadata travels in the
/// <c>X-Total-Count</c> and <c>Link</c> (RFC 8288) headers written by <see cref="WriteHeaders"/>.
/// </summary>
public sealed record PageResponse<T>
{
    public const string TotalCountHeader = "X-Total-Count";
    public const string LinkHeader = "Link";

    public PageResponse(IReadOnlyList<T> items, int page, int pageSize, long totalCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(totalCount);

        Items = items;
        Page = page;
        PageSize = pageSize;
        TotalCount = totalCount;
    }

    public IReadOnlyList<T> Items { get; }

    public int Page { get; }

    public int PageSize { get; }

    public long TotalCount { get; }

    public long TotalPages => (TotalCount + PageSize - 1) / PageSize;

    /// <summary>
    /// Writes <c>X-Total-Count</c> and a <c>Link</c> header with first/prev/next/last relations,
    /// each link being the current request path and query with only its <c>page</c> and <c>pageSize</c>
    /// query parameters replaced, so any filter (for example <c>categoryId</c>) is preserved.
    /// </summary>
    public void WriteHeaders(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var response = httpContext.Response;
        response.Headers[TotalCountHeader] = TotalCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var links = new List<string>();
        var lastPage = Math.Max(TotalPages, 1);

        links.Add(FormatLink(httpContext.Request, 1, "first"));
        if (Page > 1)
        {
            links.Add(FormatLink(httpContext.Request, Math.Min(Page - 1, lastPage), "prev"));
        }

        if (Page < lastPage)
        {
            links.Add(FormatLink(httpContext.Request, Page + 1, "next"));
        }

        links.Add(FormatLink(httpContext.Request, lastPage, "last"));

        response.Headers[LinkHeader] = string.Join(", ", links);
    }

    private string FormatLink(HttpRequest request, long page, string relation)
    {
        var query = QueryHelpers.ParseQuery(request.QueryString.Value);
        query.Remove("page");
        query.Remove("pageSize");

        var builder = new QueryBuilder();
        foreach (var (key, values) in query)
        {
            foreach (var value in values)
            {
                builder.Add(key, value ?? string.Empty);
            }
        }

        builder.Add("page", page.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Add("pageSize", PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Relative reference (RFC 8288 allows it): behind api-gateway, an absolute URL would
        // expose the service's internal host instead of the public one the client called.
        var url = UriHelper.BuildRelative(request.PathBase, request.Path, builder.ToQueryString());
        return $"<{url}>; rel=\"{relation}\"";
    }
}
