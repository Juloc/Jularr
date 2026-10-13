using Jularr.Backend.Data.Enums.Field;
using Jularr.Backend.Data.Enums.Media;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Library
{
    public static class LibraryRootFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 128, AllowBlank = false, Trim = true };
        public static readonly EnumField MediaTypeId = new(typeof(MediaType));
        public static readonly EnumField MediaSubtypeId = new(typeof(MediaSubtype)) { Required = false };
        public static readonly StringField RelativePath = new(NpgsqlDbType.Varchar) { MaximumLength = 1024, Trim = true, Format = StringFormat.RelativePath };
        public static readonly LongField Version = new() { Minimum = 1 };
    }
}
