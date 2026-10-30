using Jularr.Backend.Data.Enums.Field;
using Jularr.Backend.Data.Enums.Provider;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Provider
{
    // A profile's own AniList account, its list and the progress agreed with it.
    public static class AniListFields
    {
        public static readonly LongField ProfileId = new() { Minimum = 1 };
        public static readonly IntField ViewerId = new() { Minimum = 1 };
        public static readonly StringField ViewerName = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly StringField RequiredViewerName = new(NpgsqlDbType.Varchar) { MaximumLength = 64 };

        // The authorization code of AniList's PIN page; write-only.
        public static readonly StringField Code = new() { MaximumLength = 4096, Trim = true, Format = StringFormat.SingleLine, AllowBlank = false };
        public static readonly StringField AuthorizationUrl = new() { Required = false };
        public static readonly BoolField IsConfigured = new();
        public static readonly BoolField IsConnected = new();
        public static readonly BoolField IsPushEnabled = new();
        public static readonly BoolField IsExpired = new();
        public static readonly DateTimeField Moment = new() { Required = false };
        public static readonly DateTimeField RequiredMoment = new();
        public static readonly StringField FailureCode = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly IntField Count = new() { Minimum = 0 };
        public static readonly IntField MediaId = new() { Minimum = 1 };
        public static readonly BoolField IsManga = new();
        public static readonly LongField ListEntryId = new() { Minimum = 1 };
        public static readonly StringField Status = new(NpgsqlDbType.Varchar) { MaximumLength = 16 };
        public static readonly IntField Progress = new() { Minimum = 0 };
        public static readonly IntField Total = new() { Minimum = 1, Required = false };
        public static readonly IntField Number = new() { Minimum = 0 };
        public static readonly BoolField IsCompleted = new();
        public static readonly LongField WorkId = new() { Minimum = 1 };
        public static readonly LongField WorkUnitId = new() { Minimum = 1, Required = false };
        public static readonly LongField OptionalWorkId = new() { Minimum = 1, Required = false };
        public static readonly StringField Title = new(NpgsqlDbType.Varchar) { MaximumLength = 500 };
        public static readonly StringField CoverUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false };
        public static readonly StringField OptionalStatus = new(NpgsqlDbType.Varchar) { MaximumLength = 16, Required = false };
        public static readonly BoolField IsOnList = new();
        public static readonly EnumField SyncStateId = new(typeof(AniListSyncStateType)) { Required = false };

        // JSON rows handed to one statement.
        public static readonly StringField Rows = new() { AllowBlank = false };
    }
}
