using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    // An AniList entry in one season of a Work's TheTVDB series; ambiguous when the entry names another series too.
    internal sealed class AnimeMappingData : BaseData
    {
        public AnimeMappingData(int aniListId, int seasonNumber, int aniListEpisode, int firstEpisode, int? lastEpisode, bool isAmbiguous)
        {
            AniListId = aniListId;
            SeasonNumber = seasonNumber;
            AniListEpisode = aniListEpisode;
            FirstEpisode = firstEpisode;
            LastEpisode = lastEpisode;
            IsAmbiguous = isAmbiguous;
        }

        [DataField(typeof(AnimeMappingFields), nameof(AnimeMappingFields.AniListId))]
        public int AniListId { get; }

        [DataField(typeof(AnimeMappingFields), nameof(AnimeMappingFields.SeasonNumber))]
        public int SeasonNumber { get; }

        [DataField(typeof(AnimeMappingFields), nameof(AnimeMappingFields.AniListEpisode))]
        public int AniListEpisode { get; }

        [DataField(typeof(AnimeMappingFields), nameof(AnimeMappingFields.FirstEpisode))]
        public int FirstEpisode { get; }

        [DataField(typeof(AnimeMappingFields), nameof(AnimeMappingFields.LastEpisode))]
        public int? LastEpisode { get; }

        [DataField(typeof(AnimeMappingFields), nameof(AnimeMappingFields.IsAmbiguous))]
        public bool IsAmbiguous { get; }
    }

    internal sealed class AnimeMappingLookupData : BaseData
    {
        public AnimeMappingLookupData(int aniListId)
        {
            AniListId = aniListId;
        }

        [DataField(typeof(AnimeMappingFields), nameof(AnimeMappingFields.AniListId))]
        public int AniListId { get; }
    }

    internal sealed class AnimeMappingSeriesData : BaseData
    {
        public AnimeMappingSeriesData(int tvdbId)
        {
            TvdbId = tvdbId;
        }

        [DataField(typeof(AnimeMappingFields), nameof(AnimeMappingFields.TvdbId))]
        public int TvdbId { get; }
    }

    // The loaded release: its ETag for the next conditional download and how many rows it gave.
    internal sealed class AnimeMappingSourceData : BaseData
    {
        public AnimeMappingSourceData(string? eTag, long rows)
        {
            ETag = eTag;
            Rows = rows;
        }

        [DataField(typeof(AnimeMappingFields), nameof(AnimeMappingFields.ETag))]
        public string? ETag { get; }

        [DataField(typeof(AnimeMappingFields), nameof(AnimeMappingFields.Rows))]
        public long Rows { get; }
    }

    // A new release replacing every row: JSON rows of AniListId, TvdbId, SeasonNumber, AniListEpisode, FirstEpisode and LastEpisode.
    internal sealed class AnimeMappingLoadData : BaseData
    {
        public AnimeMappingLoadData(string mappings, string? eTag, string schemaVersion, DateTime? generatedAt)
        {
            Mappings = mappings;
            ETag = eTag;
            SchemaVersion = schemaVersion;
            GeneratedAt = generatedAt;
        }

        [DataField(typeof(AnimeMappingFields), nameof(AnimeMappingFields.Mappings))]
        public string Mappings { get; }

        [DataField(typeof(AnimeMappingFields), nameof(AnimeMappingFields.ETag))]
        public string? ETag { get; }

        [DataField(typeof(AnimeMappingFields), nameof(AnimeMappingFields.SchemaVersion))]
        public string SchemaVersion { get; }

        [DataField(typeof(AnimeMappingFields), nameof(AnimeMappingFields.GeneratedAt))]
        public DateTime? GeneratedAt { get; }
    }
}
