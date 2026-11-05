using Jularr.Backend.Data.Enums.Field;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Acquisition
{
    public static class LanguageRuleFields
    {
        public static readonly StringField Language = new(NpgsqlDbType.Varchar) { MaximumLength = 2, Format = StringFormat.Language };
        public static readonly StringArrayField Entries = new() { ItemMaximumLength = 64, MaximumLength = 100 };
    }
}
