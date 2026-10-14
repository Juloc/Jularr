using Jularr.Backend.Data.Enums.Reading;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Reading
{
    public static class ReadingAnnotationFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly IntField Position = new() { Minimum = 0, Maximum = 10_000_000 };
        public static readonly StringField Quote = new(NpgsqlDbType.Varchar) { MaximumLength = 1000, AllowBlank = false, Trim = true };
        public static readonly StringField Note = new(NpgsqlDbType.Varchar) { MaximumLength = 4000, Required = false, Trim = true };
        public static readonly EnumField HighlightColorTypeId = new(typeof(HighlightColorType));
        public static readonly DateTimeField CreatedAt = new();
    }
}
