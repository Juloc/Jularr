using Npgsql;
using NpgsqlTypes;

namespace Jularr.Web.Data;

public sealed record PageRequest
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;

    public int Page { get; }

    public int PageSize { get; }

    public long Offset { get; }

    public PageRequest(int page = 1, int pageSize = DefaultPageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaximumPageSize);

        Page = page;
        PageSize = pageSize;
        Offset = ((long)page - 1) * pageSize;
    }

    public NpgsqlParameter[] ToSqlParameters() =>
    [
        new NpgsqlParameter("PageSize", NpgsqlDbType.Integer)
        {
            Value = PageSize
        },
        new NpgsqlParameter("Offset", NpgsqlDbType.Bigint)
        {
            Value = Offset
        }
    ];
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
