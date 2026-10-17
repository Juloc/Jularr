using Jularr.Backend.Data.Enums.Media;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class ArtworkFields
    {
        public static readonly IntField Count = new() { Minimum = 0 };
        public static readonly BoolField IsExclusive = new();
        public static readonly LongField ArtworkId = new() { Minimum = 1 };
        public static readonly IntField Width = new() { Minimum = 16, Maximum = 8000 };
        public static readonly IntField Height = new() { Minimum = 16, Maximum = 8000 };
        public static readonly LongField SourceSizeBytes = new() { Minimum = 0, Required = false };
        public static readonly LongField CacheLimitBytes = new() { Minimum = 0, Maximum = 1099511627776 };
        public static readonly EnumField CacheQualityId = new(typeof(ArtworkQuality));
        public static readonly EnumField CacheFormatId = new(typeof(ArtworkCacheFormat));
        public static readonly BoolField ProgressiveJxl = new();
        public static readonly EnumField FormatId = new(typeof(ArtworkFormat));
        public static readonly BoolField IsProgressive = new();
        public static readonly EnumField SlotId = new(typeof(ArtworkSlot));
        public static readonly StringField CacheKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, MinimumLength = 64, AllowBlank = false, Required = false };
        public static readonly StringField ContentHash = new(NpgsqlDbType.Varchar) { MaximumLength = 64, MinimumLength = 64, AllowBlank = false };
        public static readonly ByteArrayField FallbackBytes = new() { Required = false };
        public static readonly BoolField ExportToNas = new();
        public static readonly EnumField NasModeId = new(typeof(NasArtworkMode));
        public static readonly StringField SourceUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 4000, AllowBlank = false };
        public static readonly StringField Folder = new(NpgsqlDbType.Varchar) { MaximumLength = 2048, AllowBlank = true };
    }
}
