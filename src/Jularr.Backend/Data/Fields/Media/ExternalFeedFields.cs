using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class ExternalFeedFields
    {
        public static readonly StringField Key = new(NpgsqlDbType.Varchar) { AllowBlank = false, MaximumLength = 200 };
        public static readonly StringField Language = new(NpgsqlDbType.Varchar) { AllowBlank = false, MaximumLength = 35, Required = false };
        // JSON rows of a feed's entries: Position, WorkId and SeasonNumber.
        public static readonly StringField Items = new() { AllowBlank = false };
        public static readonly StringArrayField WholeCatalogs = new() { ItemMaximumLength = 16, MaximumLength = 16 };
    }
}
