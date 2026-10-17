using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class DiscoverySnapshotFields
    {
        public static readonly StringField Key = new(NpgsqlDbType.Varchar) { MaximumLength = 64, AllowBlank = false };
        public static readonly StringField Request = new() { MaximumLength = 4096, AllowBlank = false };
        public static readonly StringField Payload = new() { MaximumLength = 1_048_576, AllowBlank = false };
        public static readonly IntField FreshHours = new() { Minimum = 1, Maximum = 8760 };
        public static readonly BoolField IsFresh = new();
    }
}
