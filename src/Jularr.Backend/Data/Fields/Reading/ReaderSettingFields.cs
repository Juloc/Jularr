using Jularr.Backend.Data.Enums.Reading;

namespace Jularr.Backend.Data.Fields.Reading
{
    public static class ReaderSettingFields
    {
        public static readonly EnumField FontTypeId = new(typeof(ReaderFont));
        public static readonly IntField Size = new() { Minimum = 14, Maximum = 40 };
        public static readonly IntField LineSpacing = new() { Minimum = 120, Maximum = 220 };
        public static readonly IntField ParagraphSpacing = new() { Minimum = 0, Maximum = 200 };
        public static readonly IntField TextWidth = new() { Minimum = 40, Maximum = 90 };
        public static readonly BoolField Hyphenation = new();
        public static readonly BoolField IsSaved = new();
    }
}
