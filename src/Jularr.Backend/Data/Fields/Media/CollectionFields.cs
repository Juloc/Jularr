using Jularr.Backend.Data.Enums.Media;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class CollectionFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 200, AllowBlank = false, Trim = true };
        public static readonly BoolField IsSmart = new();
        public static readonly EnumField MediaTypeId = new(typeof(MediaType)) { Required = false };
        public static readonly EnumField MediaSubtypeId = new(typeof(MediaSubtype)) { Required = false };
        public static readonly IntField ReleaseYear = new() { Minimum = 1800, Maximum = 2200, Required = false };
        public static readonly BoolField OnlyAvailable = new();
        public static readonly BoolField OnlyUnfinished = new();
        public static readonly IntField WorkCount = new() { Minimum = 0, Required = false };
        public static readonly BoolField ContainsWork = new();
        public static readonly LongField Version = new() { Minimum = 1 };
    }
}
