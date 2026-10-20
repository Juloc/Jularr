using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    // The cache of images of titles that are not Works.
    public static class CoverCacheFields
    {
        public static readonly IntField KeepDays = new() { Minimum = 1, Maximum = 365 };
        public static readonly IntField MaximumMegabytes = new() { Minimum = 0, Maximum = 1_000_000 };
        public static readonly LongField Version = new() { Minimum = 1 };
        public static readonly StringField ImageUrl = new(NpgsqlDbType.Varchar) { AllowBlank = false, MaximumLength = 500 };
        public static readonly IntField Width = new() { Minimum = 1, Maximum = 4000, Required = false };
    }
}
