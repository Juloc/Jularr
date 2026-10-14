using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class FranchiseFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 300, AllowBlank = false, Trim = true };
        public static readonly IntField TmdbCollectionId = new() { Minimum = 1 };
    }
}
