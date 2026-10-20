using Jularr.Backend.Data.Enums.Field;
using Jularr.Backend.Data.Enums.Library;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class WorkManagementFields
    {
        public static readonly EnumField WorkUnitStatusId = new(typeof(WorkUnitStatus));
        public static readonly EnumField FileQualityGradeId = new(typeof(FileQualityGrade));
        public static readonly EnumField FileSizeGradeId = new(typeof(FileSizeGrade));
        public static readonly DecimalField MegabytesPerMinute = new() { Minimum = 0, Scale = 1, Required = false };
        public static readonly StringField Codec = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly StringField DynamicRange = new(NpgsqlDbType.Varchar) { MaximumLength = 16, Required = false };
        public static readonly IntField Height = new() { Minimum = 1, Required = false };
        public static readonly StringArrayField Languages = new() { ItemMaximumLength = 16, MaximumLength = 64 };
        public static readonly StringField Overview = new() { MaximumLength = 4000, Required = false };
        public static readonly StringField Genres = new() { MaximumLength = 1000, Required = false };
        public static readonly StringField Studio = new() { MaximumLength = 500, Required = false };
        public static readonly StringField ImageUrl = new() { MaximumLength = 500, Required = false };
        public static readonly LongField UnitCount = new() { Minimum = 0 };
        public static readonly LongArrayField UnitIds = new() { Minimum = 1, MaximumLength = 1000 };
        public static readonly BoolField IsMissing = new();
    }
}
