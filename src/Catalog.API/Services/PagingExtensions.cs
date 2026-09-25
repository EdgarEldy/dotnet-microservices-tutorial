using Catalog.API.Dtos;
using Common.Lib.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Catalog.API.Services;

internal static class PagingExtensions
{
    /// <summary>
    /// Counts the (already filtered and ordered) query, then reads the requested page. A page
    /// past the end is an empty list with the real total, not an error.
    /// </summary>
    public static async Task<PageResponse<T>> ToPageAsync<T>(
        this IQueryable<T> query,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);

        // Computed in 64 bits: page * pageSize can exceed int.MaxValue for a large page number.
        var offset = (long)(request.Page - 1) * request.PageSize;
        var items = offset >= totalCount
            ? []
            : await query.Skip((int)offset).Take(request.PageSize).ToListAsync(cancellationToken);

        return new PageResponse<T>(items, request.Page, request.PageSize, totalCount);
    }
}
