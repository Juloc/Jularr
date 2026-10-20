using Jularr.Backend.Data.Enums.Media;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Library
{
    public static class NamingPatternFields
    {
        public static readonly EnumField MediaTypeId = new(typeof(MediaType));
        public static readonly EnumField MediaSubtypeId = new(typeof(MediaSubtype)) { Required = false };
        public static readonly BoolField ReplacesIllegalCharacters = new();
        public static readonly EnumField ColonReplacementId = new(typeof(Enums.Library.ColonReplacement));
        public static readonly StringField CustomColonReplacement = new() { MaximumLength = 16, Required = false, Format = Enums.Field.StringFormat.SingleLine };
        public static readonly StringField SpecialsFolder = new() { MaximumLength = 60, Required = false, Trim = true, Format = Enums.Field.StringFormat.SingleLine };
        public static readonly StringField Pattern = new(NpgsqlDbType.Varchar) { MaximumLength = 500, AllowBlank = false, Trim = true };
        // The folders below the library, separated by slashes, and the file name; blank folders put files at the top.
        public static readonly StringField Folder = new() { MaximumLength = 400, Required = false, Trim = true, Format = Enums.Field.StringFormat.SingleLine };
        public static readonly StringField File = new() { MaximumLength = 300, AllowBlank = false, Trim = true, Format = Enums.Field.StringFormat.SingleLine };
        public static readonly StringField Example = new() { MaximumLength = 1000, Required = false };
        public static readonly LongField Version = new() { Minimum = 1 };
    }
}
