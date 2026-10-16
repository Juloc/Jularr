using NpgsqlTypes;
using Jularr.Backend.Data.Fields;
using Jularr.Backend.Data.Enums.Field;

namespace Jularr.Backend.Data.Fields.Localization
{
    public static class UiLocaleFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Locale = new(NpgsqlDbType.Varchar) { MaximumLength = 32, AllowBlank = false, Format = StringFormat.Locale };
        public static readonly StringField Name = new() { AllowBlank = false, Trim = true };
        public static readonly BoolField IsEnabled = new();
        public static readonly BoolField IsRightToLeft = new();
        public static readonly LongField CatalogVersion = new() { Minimum = 1 };
    }

    // Derived status of the locale's translation catalog.
    public static class UiLocaleTranslationFields
    {
        public static readonly BoolField IsMachineTranslated = new();
    }
}
