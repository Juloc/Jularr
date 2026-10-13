using Jularr.Backend.Data.Enums.Reading;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Reading
{
    public static class ReadingFields
    {
        public static readonly LongField DocumentId = new() { Minimum = 1 };
        public static readonly ByteArrayField ContentHash = new() { MinimumLength = 32, MaximumLength = 32 };
        public static readonly IntField AnalyzerVersion = new() { Minimum = 1 };
        public static readonly EnumField ReadingFormatTypeId = new(typeof(ReadingFormatType));
        public static readonly StringField Title = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false };
        public static readonly StringField Language = new(NpgsqlDbType.Varchar) { MaximumLength = 35, Required = false };
        public static readonly StringField Structure = new() { AllowBlank = false };
        public static readonly LongField TotalCharacters = new() { Minimum = 0 };
        public static readonly LongField CacheBytes = new() { Minimum = 0 };
        public static readonly LongField SizeBytes = new() { Minimum = 0 };
        public static readonly DateTimeField CacheCompletedAt = new() { Required = false };
        public static readonly DateTimeField ModifiedAt = new();
        public static readonly BoolField IsCached = new();
        public static readonly BoolField IsMissing = new();
        public static readonly StringField Path = new(NpgsqlDbType.Varchar) { MaximumLength = 1024, AllowBlank = false };
        public static readonly StringField MediaType = new(NpgsqlDbType.Varchar) { MaximumLength = 128, AllowBlank = false };
        public static readonly ByteArrayField Bytes = new();
        public static readonly StringField Html = new() { AllowBlank = true };
        public static readonly IntField SectionIndex = new() { Minimum = 0, Maximum = 100_000 };
        public static readonly IntField SectionCount = new() { Minimum = 0, Maximum = 100_000 };
        public static readonly IntField Level = new() { Minimum = 0, Maximum = 10 };
        public static readonly StringField Anchor = new() { MaximumLength = 256, Required = false };
        public static readonly StringField Locator = new(NpgsqlDbType.Varchar) { MaximumLength = 128, Required = false };
        public static readonly BoolField IsRightToLeft = new();
        public static readonly DecimalField Progression = new() { Minimum = 0, Maximum = 1, Scale = 4 };
        public static readonly LongField CacheBudgetBytes = new() { Minimum = 0 };
        public static readonly IntField CacheBudgetWorks = new() { Minimum = 0, Maximum = 10_000 };
        public static readonly DateTimeField ProtectedSince = new();
        public static readonly IntField Count = new() { Minimum = 0 };
    }
}
