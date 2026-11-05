using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Field;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Acquisition
{
    public static class LanguageChainFields
    {
        public static readonly EnumField ModeId = new(typeof(MonitoredLanguageMode));
        public static readonly StringField FirstLanguage = new(NpgsqlDbType.Varchar) { Required = false, MaximumLength = 2, Format = StringFormat.Language };
        public static readonly StringField SecondLanguage = new(NpgsqlDbType.Varchar) { Required = false, MaximumLength = 2, Format = StringFormat.Language };
        public static readonly BoolField UsesOriginal = new();
        public static readonly BoolField HasOwnChain = new();
        public static readonly StringArrayField Languages = new() { ItemMaximumLength = 2 };
    }
}
