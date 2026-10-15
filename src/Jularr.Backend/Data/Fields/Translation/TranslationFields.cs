using Jularr.Backend.Data.Enums.Translation;

namespace Jularr.Backend.Data.Fields.Translation
{
    public static class TranslationFields
    {
        public static readonly EnumField LanguageTypeId = new(typeof(TranslationLanguageType));
        public static readonly IntField Step = new() { Minimum = 0, Maximum = 2 };
        public static readonly StringField FileType = new() { MaximumLength = 16, AllowBlank = false };
        public static readonly StringField Url = new() { MaximumLength = 200, AllowBlank = false };
        public static readonly StringField Precision = new() { MaximumLength = 32, Required = false };
        public static readonly StringField Name = new() { MaximumLength = 80, AllowBlank = false, Trim = true };

        // A section's blocks in order as a JSON array of plain texts.
        public static readonly StringField Blocks = new() { MaximumLength = 4_000_000, AllowBlank = false };
    }
}
