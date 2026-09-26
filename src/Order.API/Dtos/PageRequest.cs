namespace Order.API.Dtos;

/// <summary>
/// The page/pageSize query parameters of a paginated list. Out-of-range values are rejected
/// with a 400 by the validator, never silently clamped.
/// </summary>
public record PageRequest
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public int Page { get; init; } = DefaultPage;

    public int PageSize { get; init; } = DefaultPageSize;
}
