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
        public static readonly IntField AbsoluteNumber = new() { Minimum = 1, Required = false };
        public static readonly IntField DiscNumber = new() { Minimum = 1, Required = false };
        public static readonly EnumField WorkGroupKindId = new(typeof(WorkGroupKind));
        public static readonly LongField GroupId = new() { Minimum = 1, Required = false };
        public static readonly StringField GroupName = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false };
        public static readonly StringField LanguageCode = new(NpgsqlDbType.Varchar) { MaximumLength = 3, Required = false };
        public static readonly EnumField EditionFormatId = new(typeof(Jularr.Backend.Data.Enums.Media.EditionFormat)) { Required = false };
        public static readonly StringField Publisher = new(NpgsqlDbType.Varchar) { MaximumLength = 300, Required = false };
        public static readonly StringField Isbn = new() { MaximumLength = 400, Required = false };
        public static readonly BoolField IsRomVerified = new();
        public static readonly StringField TranslationLanguage = new(NpgsqlDbType.Varchar) { MaximumLength = 35 };
        public static readonly IntField CoveragePercent = new() { Minimum = 0, Maximum = 100 };
        public static readonly LongField TranslationBytes = new() { Minimum = 0 };
        public static readonly StringField GameRegion = new(NpgsqlDbType.Varchar) { MaximumLength = 100, Required = false };
        public static readonly StringField GameVersion = new(NpgsqlDbType.Varchar) { MaximumLength = 100, Required = false };
    }
}
