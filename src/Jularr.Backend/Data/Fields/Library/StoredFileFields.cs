using Jularr.Backend.Data.Enums.Field;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Library
{
    public static class StoredFileFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField RelativePath = new(NpgsqlDbType.Varchar) { MaximumLength = 2048, AllowBlank = false, Format = StringFormat.RelativePath };
        public static readonly LongField SizeBytes = new() { Minimum = 0 };
        public static readonly DateTimeField ModifiedAt = new();
        public static readonly DateTimeField LastSeenAt = new();
        public static readonly DateTimeField MissingSince = new() { Required = false };
        public static readonly DateTimeField ProbedAt = new() { Required = false };
        public static readonly StringField Container = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly LongField DurationMs = new() { Minimum = 0, Required = false };
        public static readonly LongField BitRate = new() { Minimum = 0, Required = false };
        public static readonly StringField Sha1 = new(NpgsqlDbType.Varchar) { MaximumLength = 40, Required = false };
        public static readonly BoolField IsAvailable = new();
        public static readonly BoolField HasOtherVersions = new();
        public static readonly BoolField OnlyUnmatched = new();
        public static readonly LongField FileCount = new() { Minimum = 0 };
        public static readonly StringField Payload = new() { AllowBlank = false };
        public static readonly LongField AfterId = new() { Minimum = 0 };
        public static readonly IntField TrickplayFrames = new() { Minimum = 1 };
        public static readonly StringField TargetPath = new(NpgsqlDbType.Varchar) { MaximumLength = 2048, AllowBlank = false, Format = StringFormat.RelativePath };
        public static readonly StringField RenameFingerprint = new() { MinimumLength = 64, MaximumLength = 64, AllowBlank = false, Format = StringFormat.SingleLine };
        public static readonly IntField RenamedCount = new() { Minimum = 0 };
        public static readonly IntField SkippedCount = new() { Minimum = 0 };
        public static readonly StringField ReleaseGroup = new() { MaximumLength = 64, Required = false };
        public static readonly StringField QualityKey = new() { MaximumLength = 64, Required = false };
        public static readonly StringField Edition = new() { MaximumLength = 64, Required = false };
        public static readonly IntField Revision = new() { Minimum = 1, Maximum = 9 };
        public static readonly LongArrayField Ids = new() { Minimum = 1, MaximumLength = 500, IsSet = true };
        public static readonly BoolField IsMissing = new();
        public static readonly BoolField IsMedia = new();
        public static readonly IntField DeletedCount = new() { Minimum = 0 };
        public static readonly IntField KeptCount = new() { Minimum = 0 };
        public static readonly IntField UpdatedCount = new() { Minimum = 0 };
        public static readonly IntField PageCount = new() { Minimum = 1, Required = false };
        public static readonly StringField TrackLanguages = new() { MaximumLength = 4000, Required = false };
        // The languages a person chose for a file, three-letter codes of FileLanguages.
        public static readonly StringArrayField ChosenLanguages = new() { ItemMaximumLength = 3, MaximumLength = 12 };
        public static readonly StringField AudioLanguage = new(NpgsqlDbType.Varchar) { MaximumLength = 3, MinimumLength = 3, Required = false, Trim = true };

        // Subtitles searched by hand.
        public static readonly StringField SubtitleLanguage = new(NpgsqlDbType.Varchar) { MaximumLength = 8, AllowBlank = false, Trim = true, Format = StringFormat.Locale };
        public static readonly StringArrayField SubtitleLanguages = new() { ItemMaximumLength = 8, MaximumLength = 32 };
        public static readonly StringField SubtitleOfferId = new(NpgsqlDbType.Varchar) { MaximumLength = 32, AllowBlank = false, Trim = true, Format = StringFormat.SingleLine };
        public static readonly BoolField IsHearingImpaired = new();
        public static readonly LongField Downloads = new() { Minimum = 0 };
    }
}
