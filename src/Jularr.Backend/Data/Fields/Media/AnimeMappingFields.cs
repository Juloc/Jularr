using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    // Where AniList's anime entries lie at TheTVDB, from the AniBridge mappings.
    public static class AnimeMappingFields
    {
        public static readonly IntField AniListId = new() { Minimum = 1 };
        public static readonly IntField TvdbId = new() { Minimum = 1 };
        public static readonly IntField SeasonNumber = new() { Minimum = 0 };
        public static readonly IntField AniListEpisode = new() { Minimum = 1 };
        public static readonly IntField FirstEpisode = new() { Minimum = 1 };
        public static readonly IntField LastEpisode = new() { Minimum = 1, Required = false };
        public static readonly BoolField IsAmbiguous = new();
        public static readonly StringField Mappings = new() { AllowBlank = false };
        public static readonly StringField ETag = new(NpgsqlDbType.Varchar) { MaximumLength = 200, Required = false };
        public static readonly StringField SchemaVersion = new(NpgsqlDbType.Varchar) { AllowBlank = false, MaximumLength = 32 };
        public static readonly DateTimeField GeneratedAt = new() { Required = false };
        public static readonly LongField Rows = new() { Minimum = 0 };
    }
}
