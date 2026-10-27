using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    internal class ExternalFeedData : BaseData
    {
        public ExternalFeedData(string @namespace, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, DiscoverFeedType? discoverFeedTypeId, int page, string? sourceValue, string? language)
        {
            Namespace = @namespace;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            DiscoverFeedTypeId = discoverFeedTypeId;
            Page = page;
            SourceValue = sourceValue;
            Language = language;
            Key = string.Join('/', @namespace, ((short)mediaTypeId).ToString(CultureInfo.InvariantCulture),
                ((short?)mediaSubtypeId ?? 0).ToString(CultureInfo.InvariantCulture), ((short?)discoverFeedTypeId ?? 0).ToString(CultureInfo.InvariantCulture),
                page.ToString(CultureInfo.InvariantCulture), sourceValue ?? "", language ?? "");
        }

        [DataField(typeof(ExternalFeedFields), nameof(ExternalFeedFields.Key))]
        public string Key { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.DiscoverFeedTypeId), false)]
        public DiscoverFeedType? DiscoverFeedTypeId { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.Page))]
        public int Page { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Value), false)]
        public string? SourceValue { get; }

        [DataField(typeof(ExternalFeedFields), nameof(ExternalFeedFields.Language))]
        public string? Language { get; }
    }

    // A feed's entries in their order as the Works they are; entries that are the same Work (two parts of one anime season) list it once.
    internal sealed class ExternalFeedUpdateData : ExternalFeedData
    {
        public ExternalFeedUpdateData(ExternalFeedData feed, IReadOnlyList<ExternalWorkData> works, IReadOnlyList<ExternalWorkSavedData> saved)
            : base(feed.Namespace, feed.MediaTypeId, feed.MediaSubtypeId, feed.DiscoverFeedTypeId, feed.Page, feed.SourceValue, feed.Language)
        {
            var known = saved.ToDictionary(work => work.Value, StringComparer.Ordinal);
            var items = works.Select(work => work.Value).Distinct(StringComparer.Ordinal)
                .Select(value => known.TryGetValue(value, out var work) ? work : throw new InvalidOperationException("The feed contains an unknown provider identity."))
                .DistinctBy(work => (work.WorkId, work.SeasonNumber))
                .Select((work, index) => new { Position = index + 1, work.WorkId, work.SeasonNumber })
                .ToArray();
            Count = items.Length;
            Items = JsonSerializer.Serialize(items);
        }

        public int Count { get; }

        [DataField(typeof(ExternalFeedFields), nameof(ExternalFeedFields.Items))]
        public string Items { get; }
    }

    // Whether the feed's catalog keeps its Discover lists (not on the lean tier), and the feed once it was filled.
    internal sealed class ExternalFeedStateData(bool isKept, long? feedId)
    {
        public bool IsKept { get; } = isKept;
        public long? FeedId { get; } = feedId;
    }

    internal sealed class ExternalFeedRetryData : ExternalFeedData
    {
        public ExternalFeedRetryData(ExternalFeedData feed, DateTime retryAfter)
            : base(feed.Namespace, feed.MediaTypeId, feed.MediaSubtypeId, feed.DiscoverFeedTypeId, feed.Page, feed.SourceValue, feed.Language)
        {
            RetryAfter = retryAfter;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.FetchedAt))]
        public DateTime RetryAfter { get; }
    }

    internal sealed class ExternalFeedRefreshData : BaseData
    {
        public ExternalFeedRefreshData(DateTime now, int limit, IReadOnlyList<ExternalFeedData> defaults, IReadOnlySet<string>? wholeCatalogs = null)
        {
            Now = now;
            Limit = limit;
            Feeds = JsonSerializer.Serialize(defaults);
            WholeCatalogs = wholeCatalogs?.ToArray() ?? [];
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.FetchedAt))]
        public DateTime Now { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Limit))]
        public int Limit { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Shelves))]
        public string Feeds { get; }

        [DataField(typeof(ExternalFeedFields), nameof(ExternalFeedFields.WholeCatalogs))]
        public string[] WholeCatalogs { get; }
    }
}
