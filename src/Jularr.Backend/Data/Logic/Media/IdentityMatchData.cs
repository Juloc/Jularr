using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    // A Work waiting for its AniList entries.
    internal sealed class IdentityMatchTargetData : BaseData
    {
        public IdentityMatchTargetData(long workId, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string canonicalTitle, int? releaseYear, string[] titles)
        {
            WorkId = workId;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
            Titles = titles;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Titles))]
        public string[] Titles { get; }
    }

    internal sealed class IdentityMatchQueryData : BaseData
    {
        public IdentityMatchQueryData(long? workId, int limit)
        {
            WorkId = workId;
            Limit = limit;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        [DataField(typeof(IdentityMatchFields), nameof(IdentityMatchFields.Limit))]
        public int Limit { get; }
    }

    // A matched Work whose AniList relations are due, with the AniList entries it holds (the entry of a film, a book, or the entries of its seasons).
    internal sealed class IdentityRelationTargetData : BaseData
    {
        public IdentityRelationTargetData(long workId, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, int[] providerIds)
        {
            WorkId = workId;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            ProviderIds = providerIds;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(IdentityMatchFields), nameof(IdentityMatchFields.ProviderIds))]
        public int[] ProviderIds { get; }
    }

    internal sealed class SeasonEpisodesData : BaseData
    {
        public SeasonEpisodesData(int seasonNumber, int episodes)
        {
            SeasonNumber = seasonNumber;
            Episodes = episodes;
        }

        [DataField(typeof(IdentityMatchFields), nameof(IdentityMatchFields.SeasonNumber))]
        public int SeasonNumber { get; }

        [DataField(typeof(IdentityMatchFields), nameof(IdentityMatchFields.Episodes))]
        public int Episodes { get; }
    }

    // The outcome of one matching pass; a Work-level entry or season entries with their episode offsets.
    internal sealed class IdentityMatchResultData : BaseData
    {
        public IdentityMatchResultData(long workId, string @namespace, IdentityMatchStatusType identityMatchStatusTypeId, string candidates, string? value, string seasons, bool isManual,
            bool replacesAutomatic = false)
        {
            ReplacesAutomatic = replacesAutomatic;
            WorkId = workId;
            Namespace = @namespace;
            IdentityMatchStatusTypeId = identityMatchStatusTypeId;
            Candidates = candidates;
            Value = value;
            Seasons = seasons;
            IsManual = isManual;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(IdentityMatchFields), nameof(IdentityMatchFields.IdentityMatchStatusTypeId))]
        public IdentityMatchStatusType IdentityMatchStatusTypeId { get; }

        // JSON rows of Id, Title, Year and Format for a person to choose from.
        [DataField(typeof(IdentityMatchFields), nameof(IdentityMatchFields.Candidates))]
        public string Candidates { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Value), false)]
        public string? Value { get; }

        // JSON rows of Season, Value and Offset.
        [DataField(typeof(IdentityMatchFields), nameof(IdentityMatchFields.Seasons))]
        public string Seasons { get; }

        [DataField(typeof(IdentityMatchFields), nameof(IdentityMatchFields.IsManual))]
        public bool IsManual { get; }

        // The seasons come from the AniBridge mappings and replace the Work's earlier automatic ones; a person's stay.
        [DataField(typeof(IdentityMatchFields), nameof(IdentityMatchFields.ReplacesAutomatic))]
        public bool ReplacesAutomatic { get; }
    }
}
