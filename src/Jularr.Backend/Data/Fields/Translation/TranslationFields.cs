using Jularr.Backend.Data.Enums.Field;

namespace Jularr.Backend.Data.Fields.Translation
{
    public static class TranslationFields
    {
        // A language as Mozilla's models name it: two or three letters, such as "de" or "ja".
        public static readonly StringField Language = new() { MaximumLength = 3, MinimumLength = 2, AllowBlank = false, Format = StringFormat.Locale };
        public static readonly StringArrayField Languages = new() { ItemMaximumLength = 3 };
        public static readonly IntField Step = new() { Minimum = 0, Maximum = 2 };
        public static readonly StringField FileType = new() { MaximumLength = 16, AllowBlank = false };
        public static readonly StringField Url = new() { MaximumLength = 200, AllowBlank = false };
        public static readonly StringField Precision = new() { MaximumLength = 32, Required = false };
        public static readonly StringField Name = new() { MaximumLength = 80, AllowBlank = false, Trim = true };
        // A title and a count of sections as the progress view reads them.
        public static readonly StringField Title = new() { MaximumLength = 500, AllowBlank = true };
        public static readonly StringField Volume = new() { MaximumLength = 16, Required = false };
        public static readonly IntField Count = new() { Minimum = 0, Maximum = 10_000_000 };
        // An Ollama machine by its place in the Owner's order, from 0.
        public static readonly IntField Machine = new() { Minimum = 0, Maximum = 7 };

        // A section's blocks in order as a JSON array of plain texts.
        public static readonly StringField Blocks = new() { MaximumLength = 4_000_000, AllowBlank = false };
        // A section's cached translation so far, as the background job reads it; none before its first block.
        public static readonly StringField Cache = new() { MaximumLength = 4_000_000, Required = false };
    }
}
