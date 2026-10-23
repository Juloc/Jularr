using NpgsqlTypes;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class WorkListFields
    {
        public static readonly StringField Query = new(NpgsqlDbType.Varchar) { MaximumLength = 100, Trim = true, Required = false };
        public static readonly BoolField IsMonitoredFilter = new() { Required = false };
        public static readonly EnumField WorkListStatusFilterId = new(typeof(WorkListStatus)) { Required = false };
        public static readonly EnumField WorkListStatusId = new(typeof(WorkListStatus));
        public static readonly EnumField WorkSortTypeId = new(typeof(WorkSortType));
        public static readonly BoolField IsDescending = new();
        public static readonly BoolField IsMonitored = new();
        public static readonly IntField FileCount = new() { Minimum = 0 };
        public static readonly LongField SizeBytes = new() { Minimum = 0 };
        public static readonly IntField TargetCount = new() { Minimum = 0 };
        public static readonly IntField TitleCount = new() { Minimum = 0 };
        public static readonly BoolField Fits = new();
        public static readonly StringField ProfileName = new(NpgsqlDbType.Varchar) { MaximumLength = 128, Required = false };
        public static readonly StringField LibraryName = new(NpgsqlDbType.Varchar) { MaximumLength = 128, Required = false };
        public static readonly LongArrayField WorkIds = new() { Minimum = 1, MaximumLength = 100, IsSet = true };
        public static readonly LongArrayField HeavyWorkIds = new() { Minimum = 1, MaximumLength = 25, IsSet = true };
        public static readonly StringField OutcomeKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, AllowBlank = false };
    }
}
