using Jularr.Backend.Data.Fields;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Instance
{
    public static class InstanceSettingsFields
    {
        public static readonly LongField Id = new() { Minimum = 1, Maximum = 1 };
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 128, AllowBlank = false, Trim = true };
        public static readonly LongField UiLocaleId = new() { Minimum = 1 };
        public static readonly LongField Version = new() { Minimum = 1 };
        public static readonly IntField ReadingCacheMegabytes = new() { Minimum = 0, Maximum = 1_048_576 };
        public static readonly IntField ReadingCacheWorks = new() { Minimum = 0, Maximum = 10_000 };
        public static readonly BoolField UsersConfirmSources = new();
        public static readonly IntField LibraryScanIntervalHours = new() { Minimum = 0, Maximum = 720 };
        public static readonly IntField HistoryRetentionDays = new() { Minimum = 1, Maximum = 3650 };
        public static readonly IntField HistoryMegabytes = new() { Minimum = 1, Maximum = 100000 };
        public static readonly IntField CollectionLimit = new() { Minimum = 1, Maximum = 1000 };
        public static readonly IntField UnitGroupLimit = new() { Minimum = 1, Maximum = 1000 };
        public static readonly StringArrayField SubtitleLanguages = new() { ItemMaximumLength = 8, MaximumLength = 10, SplitsCommas = true };
        public static readonly BoolField KeepsReleases = new();
        public static readonly EnumField RecycleModeId = new(typeof(Enums.Library.RecycleMode));
        public static readonly LongField RecycleStorageMountId = new() { Minimum = 1, Required = false };
        public static readonly StringField RecyclePath = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false, Trim = true, Format = Enums.Field.StringFormat.RelativePath };
        public static readonly IntField RecycleDays = new() { Minimum = 0, Maximum = 3650 };
    }
}
