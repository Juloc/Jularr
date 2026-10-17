using Jularr.Backend.Data.Enums.Media;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class ArtworkFields
    {
        public static readonly IntField Count = new() { Minimum = 0 };
        public static readonly BoolField IsExclusive = new();
        public static readonly BoolField ExportToNas = new();
        public static readonly EnumField NasModeId = new(typeof(NasArtworkMode));
        public static readonly StringField SourceUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 4000, AllowBlank = false };
        public static readonly StringField Folder = new(NpgsqlDbType.Varchar) { MaximumLength = 2048, AllowBlank = true };
    }
}
