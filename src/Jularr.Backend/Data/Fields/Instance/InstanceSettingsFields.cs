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
    }
}
