using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class StatisticsFields
    {
        public static readonly IntField Days = new() { Minimum = 0, Maximum = 3650 };
        public static readonly LongField DurationMs = new() { Minimum = 0 };
        public static readonly LongField Count = new() { Minimum = 0 };
        public static readonly LongField Bytes = new() { Minimum = 0 };
        public static readonly BoolField IsAvailable = new();
        public static readonly DateTimeField At = new() { Required = false };
        public static readonly StringField ToolKey = new(NpgsqlDbType.Varchar) { MaximumLength = 32, AllowBlank = false };
    }
}
