using Jularr.Backend.Data.Enums.Media;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Library
{
    public static class NamingPatternFields
    {
        public static readonly EnumField MediaTypeId = new(typeof(MediaType));
        public static readonly StringField Pattern = new(NpgsqlDbType.Varchar) { MaximumLength = 500, AllowBlank = false, Trim = true };
        public static readonly LongField Version = new() { Minimum = 1 };
    }
}
