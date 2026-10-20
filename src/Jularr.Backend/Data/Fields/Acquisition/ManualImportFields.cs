using Jularr.Backend.Data.Enums.Field;
using Jularr.Backend.Data.Enums.Storage;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Acquisition
{
    public static class ManualImportFields
    {
        public static readonly StringField Query = new(NpgsqlDbType.Varchar) { MaximumLength = 200, AllowBlank = false, Trim = true };
        public static readonly StringField FilePath = new(NpgsqlDbType.Varchar) { MaximumLength = 1024, AllowBlank = false };
        public static readonly StringField TargetName = new(NpgsqlDbType.Varchar) { MaximumLength = 1024, Required = false };
        public static readonly EnumField ImportTransferModeId = new(typeof(ImportTransferMode));
        public static readonly BoolField Imports = new();
        public static readonly IntField ImportedCount = new() { Minimum = 0 };
    }
}
