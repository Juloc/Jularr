using Jularr.Backend.Data.Enums.Media;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class WorkFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly EnumField MediaTypeId = new(typeof(MediaType));
        public static readonly StringField CanonicalTitle = new(NpgsqlDbType.Varchar) { AllowBlank = false, Trim = true, MaximumLength = 500 };
        public static readonly IntField ReleaseYear = new() { Minimum = 1800, Maximum = 2200, Required = false };
        public static readonly LongField Version = new() { Minimum = 1 };
        public static readonly DateTimeField CreatedAt = new();
        public static readonly DateTimeField UpdatedAt = new();
        public static readonly BoolField IsWatchlisted = new();
        public static readonly StringArrayField Titles = new();
    }
}
