using Jularr.Backend.Data.Enums.Media;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class WorkCoverFields
    {
        public static readonly EnumField CoverKindId = new(typeof(CoverKind));
        public static readonly EnumField FormatId = new(typeof(CoverFormat));
        public static readonly LongField WorkUnitId = new() { Minimum = 1, Required = false };
        public static readonly StringField SourceUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 500, AllowBlank = false };
        public static readonly StringField ContentType = new(NpgsqlDbType.Varchar) { MaximumLength = 32, AllowBlank = false };
        public static readonly StringField Version = new(NpgsqlDbType.Varchar) { MaximumLength = 16, AllowBlank = false };
        public static readonly StringField LocalUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 200, AllowBlank = false };
        public static readonly LongField WorkId = new() { Minimum = 1, Required = false };
        public static readonly LongField WorkIdRequired = new() { Minimum = 1 };
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 40, Required = false, Trim = true };
        public static readonly IntField Limit = new() { Minimum = 1, Maximum = 500 };
        public static readonly LongArrayField WorkIds = new();
    }
}
