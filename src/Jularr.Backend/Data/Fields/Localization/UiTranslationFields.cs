using Jularr.Backend.Data.Enums.Localization;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Localization
{
    public static class UiTranslationMessageFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Key = new(NpgsqlDbType.Varchar) { MaximumLength = 200, AllowBlank = false };
        public static readonly StringField EnglishText = new(NpgsqlDbType.Varchar) { MaximumLength = 8192, AllowBlank = false };
        public static readonly StringField Description = new(NpgsqlDbType.Varchar) { MaximumLength = 8192, AllowBlank = false, Required = false };
        public static readonly LongField Version = new() { Minimum = 1 };
        public static readonly DateTimeField UpdatedAt = new();
    }

    public static class UiTranslationFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly LongField UiLocaleId = new() { Minimum = 1 };
        public static readonly LongField UiTranslationMessageId = new() { Minimum = 1 };
        public static readonly StringField Text = new(NpgsqlDbType.Varchar) { MaximumLength = 8192, AllowBlank = false, Required = false };
        public static readonly EnumField UiTranslationStatusTypeId = new(typeof(UiTranslationStatusType));
        public static readonly LongField MessageVersion = new() { Minimum = 1 };
        public static readonly LongField Version = new() { Minimum = 1 };
        public static readonly DateTimeField UpdatedAt = new();
    }

    public static class UiTranslationStatusTypeFields
    {
        public static readonly EnumField Id = new(typeof(UiTranslationStatusType));
        public static readonly StringField Key = new() { AllowBlank = false };
    }
}
