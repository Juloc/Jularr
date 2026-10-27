using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Acquisition
{
    public static class SearchRunFields
    {
        public static readonly LongArrayField WorkIds = new() { Minimum = 1, MaximumLength = 1000, IsSet = true, Required = false };
        public static readonly StringField SelectionKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64 };
        public static readonly IntField TargetCount = new() { Minimum = 0 };
        public static readonly BoolField IsCancelled = new();
        public static readonly StringField OutcomeKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
    }
}
