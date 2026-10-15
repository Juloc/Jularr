using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    // A person the provider names for a Work: cast with their character, crew with their job.
    public static class WorkCreditFields
    {
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 300, AllowBlank = false };
        public static readonly StringField Role = new(NpgsqlDbType.Varchar) { MaximumLength = 300, Required = false };
        public static readonly StringField ImageUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false };
        public static readonly BoolField IsCrew = new();
    }
}
