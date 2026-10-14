using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class WorkUnitGroupFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 200, AllowBlank = false, Trim = true };
    }
}
