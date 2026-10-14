using Jularr.Backend.Data.Enums.Media;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class MediaAssetFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly LongField WorkEditionId = new() { Minimum = 1 };
        public static readonly LongField WorkVersionId = new() { Minimum = 1 };
        public static readonly EnumField MediaAssetTypeId = new(typeof(MediaAssetType));
        public static readonly LongField WorkUnitId = new() { Minimum = 1 };
        public static readonly EnumField WorkUnitTypeId = new(typeof(WorkUnitType));
        public static readonly DecimalField UnitNumber = new() { Minimum = 0, Maximum = 999999.999m, Scale = 3 };
        public static readonly StringField UnitTitle = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false };
        public static readonly LongArrayField Ids = new() { Minimum = 1, MaximumLength = 1000 };
    }
}
