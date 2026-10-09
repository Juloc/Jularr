using Npgsql;

namespace Jularr.Web.Data;

public sealed record PageRequest
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;
    public const long MaximumOffset = 100_000;

    public int Page { get; }

    public int PageSize { get; }

    public long Offset { get; }

    public PageRequest(int page = 1, int pageSize = DefaultPageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaximumPageSize);

        var offset = checked(((long)page - 1) * pageSize);
        if (offset > MaximumOffset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(page),
                "Page exceeds the maximum offset. Use a bounded sequential query instead.");
        }

        Page = page;
        PageSize = pageSize;
        Offset = offset;
    }

    public NpgsqlParameter[] ToSqlParameters() =>
        SqlParams.From(this)
            .Add(nameof(PageSize))
            .Add(nameof(Offset))
            .ToArray();
}

public sealed record PageResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long? TotalCount = null,
    bool? HasMore = null)
{
    public static PageResult<T> From(
        IReadOnlyList<T> items,
        PageRequest request,
        long? totalCount = null,
        bool? hasMore = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(request);

        if (items.Count > request.PageSize)
        {
            throw new ArgumentException("Page contains more items than requested.", nameof(items));
        }

        if (totalCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalCount));
        }

        return new PageResult<T>(items, request.Page, request.PageSize, totalCount, hasMore);
    }
}
