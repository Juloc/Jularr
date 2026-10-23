using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Library
{
    // One event of a Work's history and the texts it keeps.
    public static class ActivityEventFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly EnumField ActivityEventTypeId = new(typeof(ActivityEventType));
        public static readonly EnumField ActivityEventGroupTypeId = new(typeof(ActivityEventGroupType));
        public static readonly IntField EventCount = new() { Minimum = 0 };
        public static readonly DateTimeField OccurredAt = new();
        public static readonly StringField WorkTitle = new(NpgsqlDbType.Varchar) { MaximumLength = 500, AllowBlank = false };
        public static readonly EnumField WorkUnitTypeId = new(typeof(WorkUnitType)) { Required = false };
        public static readonly DecimalField UnitNumber = new() { Minimum = 0, Scale = 3, Required = false };
        public static readonly StringField ReleaseTitle = new(NpgsqlDbType.Varchar) { MaximumLength = 1000, Required = false };
        public static readonly StringField QualityKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly StringField ConnectionName = new(NpgsqlDbType.Varchar) { MaximumLength = 128, Required = false };
        public static readonly StringField Path = new(NpgsqlDbType.Varchar) { MaximumLength = 2048, Required = false };
        public static readonly StringField ReasonKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly StringField AccountName = new() { MaximumLength = 200, Required = false };
    }
}
