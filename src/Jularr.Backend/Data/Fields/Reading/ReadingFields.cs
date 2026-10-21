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
        public static readonly BoolField IsPending = new();
        public static readonly BoolField IsPdf = new();
        public static readonly StringField Path = new(NpgsqlDbType.Varchar) { MaximumLength = 1024, AllowBlank = false };
        public static readonly StringField MediaType = new(NpgsqlDbType.Varchar) { MaximumLength = 128, AllowBlank = false };
        public static readonly ByteArrayField Bytes = new();
        public static readonly StringField Html = new() { AllowBlank = true };
        public static readonly IntField SectionIndex = new() { Minimum = 0, Maximum = 100_000 };

        // "original" or a language code such as "de"; empty follows the profile's language.
        public static readonly StringField ReadingLanguage = new(NpgsqlTypes.NpgsqlDbType.Varchar) { MaximumLength = 8, Required = false, AllowBlank = false, Trim = true };
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
        public static readonly StringField Query = new() { MinimumLength = 2, MaximumLength = 100, AllowBlank = false, Trim = true, Format = Enums.Field.StringFormat.SingleLine };
        public static readonly StringField Snippet = new() { MaximumLength = 400 };
        public static readonly ByteArrayField Sha256 = new() { MinimumLength = 32, MaximumLength = 32, Required = false };
        public static readonly EnumField ReadingDerivationStatusTypeId = new(typeof(ReadingDerivationStatus));
        public static readonly EnumField ReadingDerivationStatusTypeIdOptional = new(typeof(ReadingDerivationStatus)) { Required = false };
        public static readonly StringField Reason = new(NpgsqlDbType.Varchar) { MaximumLength = 32, Required = false };
        public static readonly IntField Pages = new() { Minimum = 0, Required = false };
        public static readonly IntField Chapters = new() { Minimum = 0, Required = false };
        public static readonly IntField Figures = new() { Minimum = 0, Required = false };
        public static readonly IntField Attempts = new() { Minimum = 0 };

        // The view a profile chose for a book (none is automatic), pages of a PDF and where a page lies in the Smart Book.
        public static readonly EnumField ReadingViewTypeId = new(typeof(ReadingView));
        public static readonly IntField PageNumber = new() { Minimum = 1, Maximum = 100_000 };
        public static readonly IntField BlockIndex = new() { Minimum = 0 };
        public static readonly StringField RecognizedReason = new(NpgsqlDbType.Varchar) { MaximumLength = 24, AllowBlank = false };
        public static readonly DecimalField Quality = new() { Minimum = 0, Maximum = 1, Scale = 3 };
        public static readonly StringField RecognizedLines = new() { AllowBlank = false };
        public static readonly StringField PageNote = new() { MaximumLength = 4000, AllowBlank = false, Trim = true };
        public static readonly StringField PageAnchor = new(NpgsqlDbType.Varchar) { MaximumLength = 200, AllowBlank = false, Trim = true };
        public static readonly BoolField IsRecommended = new();
        public static readonly LongField PageNoteId = new() { Minimum = 1 };
    }
}
