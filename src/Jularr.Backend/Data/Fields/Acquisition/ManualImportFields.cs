using Jularr.Backend.Data.Enums.Field;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Acquisition
{
    public static class ManualImportFields
    {
        public static readonly StringField Query = new(NpgsqlDbType.Varchar) { MaximumLength = 200, AllowBlank = false, Trim = true };
        public static readonly StringField FilePath = new(NpgsqlDbType.Varchar) { MaximumLength = 1024, AllowBlank = false };
        public static readonly StringField TargetName = new(NpgsqlDbType.Varchar) { MaximumLength = 1024, Required = false };
        public static readonly StringField ProblemKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly StringField TargetPath = new(NpgsqlDbType.Varchar) { MaximumLength = 2048, Required = false };
        public static readonly StringField Folder = new(NpgsqlDbType.Varchar) { MaximumLength = 2048, Required = false };
        public static readonly StringField WorkTitle = new(NpgsqlDbType.Varchar) { MaximumLength = 1024, Required = false };
        public static readonly StringArrayField SearchPatterns = new() { ItemMaximumLength = 260, MaximumLength = 400 };
        public static readonly BoolField IsReadOnly = new();
        public static readonly BoolField SourceKept = new();
        public static readonly StringField Languages = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly StringField FailureKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly StringField UnplacedReason = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly BoolField Imports = new();
        public static readonly BoolField AnyMediaType = new();
        public static readonly BoolField IsHandled = new();
        public static readonly BoolField FinishesDownload = new();
        public static readonly BoolField FinishedDownload = new();
        public static readonly IntField ImportedCount = new() { Minimum = 0 };
    }
}
