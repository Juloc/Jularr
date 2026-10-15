using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class FranchiseFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 300, AllowBlank = false, Trim = true };
        public static readonly StringField Namespace = new(NpgsqlDbType.Varchar) { MaximumLength = 16, AllowBlank = false };
        public static readonly StringField ExternalId = new(NpgsqlDbType.Varchar) { MaximumLength = 300, AllowBlank = false };

        // A Work's place in its series, also between two (2.5).
        public static readonly DecimalField Position = new() { Minimum = 0, Maximum = 99999.99m, Scale = 2 };
    }
}
