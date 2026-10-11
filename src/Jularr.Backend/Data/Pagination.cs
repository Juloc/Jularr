using Jularr.Backend.Data.Common;

namespace Jularr.Backend.Data
{
    public sealed class PageRequest
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
                throw new ArgumentOutOfRangeException(nameof(page), "Page exceeds the maximum offset. Use a bounded sequential query instead.");
            }

            Page = page;
            PageSize = pageSize;
            Offset = offset;
        }
    }

    public sealed class PageResult<T> : BaseResponse
    {
        public PageResult(IReadOnlyList<T> items, PageRequest request, long? totalCount = null, bool? hasMore = null)
        {
            ArgumentNullException.ThrowIfNull(items);
            ArgumentNullException.ThrowIfNull(request);

            // Enforce page result bounds.
            if (items.Count > request.PageSize)
            {
                throw new ArgumentException("Page contains more items than requested.", nameof(items));
            }

            if (totalCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(totalCount));
            }

            Items = items;
            Page = request.Page;
            PageSize = request.PageSize;
            TotalCount = totalCount;
            HasMore = hasMore;
        }

        public IReadOnlyList<T> Items { get; }
        public int Page { get; }
        public int PageSize { get; }
        public long? TotalCount { get; }
        public bool? HasMore { get; }

        public override void Validate()
        {
            base.Validate();

            // Validate nested field data.
            foreach (var item in Items)
            {
                if (item is null)
                {
                    throw new InvalidOperationException("Page results cannot contain null items.");
                }

                if (item is BaseData data)
                {
                    data.Validate();
                }
            }
        }
    }
}
