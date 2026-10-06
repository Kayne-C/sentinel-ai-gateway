namespace Sentinel.Application.Common;

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount)
{
    public bool HasNextPage => (long)Page * PageSize < TotalCount;
}
