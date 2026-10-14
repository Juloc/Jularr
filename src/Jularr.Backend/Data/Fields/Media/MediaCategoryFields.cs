using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class MediaCategoryFields
    {
        public static readonly StringField Key = new(NpgsqlDbType.Varchar) { MaximumLength = 32 };
        public static readonly StringArrayField Keys = new() { ItemMaximumLength = 32, MaximumLength = 16 };
        public static readonly StringArrayField ChangedKeys = new() { ItemMaximumLength = 32, MaximumLength = 16, Required = false };
        public static readonly BoolField IsSubtype = new();
        public static readonly BoolField IsEnabled = new();
        public static readonly BoolField HasLibrary = new();
        public static readonly BoolField IsBlocked = new();
        public static readonly BoolField IsHidden = new();
        public static readonly LongField Version = new() { Minimum = 1 };
    }
}
