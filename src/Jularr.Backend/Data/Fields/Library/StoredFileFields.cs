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
        public static readonly BoolField OnlyUnmatched = new();
        public static readonly LongField FileCount = new() { Minimum = 0 };
        public static readonly StringField Payload = new() { AllowBlank = false };
        public static readonly LongField AfterId = new() { Minimum = 0 };
    }
}
