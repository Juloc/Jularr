using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    // One release of an indexer's recent feed: what its name and its extended attributes say.
    internal sealed class FeedEntry
    {
        public FeedEntry(string title, int? category, string? tvdbId, string? tmdbId, string? imdbId)
        {
            Title = title;
            Category = category;
            TvdbId = tvdbId;
            TmdbId = tmdbId;
            ImdbId = imdbId;
        }

        public string Title { get; }
        public int? Category { get; }
        public string? TvdbId { get; }
        public string? TmdbId { get; }
        public string? ImdbId { get; }
    }

    // A wanted target the feed can make due: its titles and provider ids.
    internal sealed class FeedTargetData : BaseData
    {
        public FeedTargetData(long wantedItemId, MediaType mediaTypeId, string canonicalTitle, string[] titles, string[] externalIdentities, string? author = null)
        {
            Author = author;
            WantedItemId = wantedItemId;
            MediaTypeId = mediaTypeId;
            CanonicalTitle = canonicalTitle;
            Titles = titles;
            ExternalIdentities = externalIdentities;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Titles))]
        public string[] Titles { get; }

        // Provider mappings as namespace:value pairs.
        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalIdentityList))]
        public string[] ExternalIdentities { get; }

        // A book's or album's author or artist, which its release names too.
        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Value), false)]
        public string? Author { get; }
    }

    internal sealed class FeedTargetQueryData : BaseData
    {
        public FeedTargetQueryData(long[] wantedItemIds)
        {
            WantedItemIds = wantedItemIds;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Ids))]
        public long[] WantedItemIds { get; }
    }

    internal sealed class FeedSeenQueryData : BaseData
    {
        public FeedSeenQueryData(long acquisitionConnectionId, string[] releaseKeys)
        {
            AcquisitionConnectionId = acquisitionConnectionId;
            ReleaseKeys = releaseKeys;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long AcquisitionConnectionId { get; }

        [DataField(typeof(FeedFields), nameof(FeedFields.ReleaseKeys))]
        public string[] ReleaseKeys { get; }
    }

    // Whether an indexer's feed was ever seen, and which of the asked releases already were.
    internal sealed class FeedSeenData : BaseData
    {
        public FeedSeenData(bool hasSeen, string[] releaseKeys)
        {
            HasSeen = hasSeen;
            ReleaseKeys = releaseKeys;
        }

        [DataField(typeof(FeedFields), nameof(FeedFields.HasSeen))]
        public bool HasSeen { get; }

        [DataField(typeof(FeedFields), nameof(FeedFields.ReleaseKeys))]
        public string[] ReleaseKeys { get; }
    }

    internal sealed class FeedRetentionData : BaseData
    {
        public FeedRetentionData(DateTime seenBefore)
        {
            SeenBefore = seenBefore;
        }

        [DataField(typeof(FeedFields), nameof(FeedFields.SeenBefore))]
        public DateTime SeenBefore { get; }
    }
}
