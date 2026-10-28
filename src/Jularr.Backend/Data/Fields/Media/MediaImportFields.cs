using Jularr.Backend.Data.Enums.Field;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class MediaImportFields
    {
        // One mebibyte of text, at most 2000 titles.
        public const int MaximumBytes = 1_048_576;
        public const int MaximumItems = 2000;

        public static readonly FileField File = new() { MaximumBytes = MaximumBytes, Extensions = [".json"] };
        public static readonly StringField Json = new() { MaximumLength = MaximumBytes };
        // Internal batched lookups may escape each Unicode character as six JSON characters.
        public static readonly StringField LookupJson = new() { MaximumLength = MaximumBytes * 6 };
        public static readonly EnumField Action = new(typeof(MediaImportActionType));
        public static readonly EnumField StatusTypeId = new(typeof(MediaImportStatusType));
        public static readonly EnumField OutcomeTypeId = new(typeof(MediaImportOutcomeType));
        public static readonly IntField Index = new() { Minimum = 0, Maximum = MaximumItems - 1 };
        public static readonly BoolField Include = new();
        public static readonly BoolField Flag = new();

        // A provider entry as "namespace:id", a local Work as "local:id", or the explicit "list:book" choice.
        public static readonly StringField Choice = new() { MaximumLength = 100, Trim = true, Format = StringFormat.SingleLine };
        public static readonly StringField Query = new() { MaximumLength = 200, Trim = true, Format = StringFormat.SingleLine };
        public static readonly StringField Key = new() { MaximumLength = 16, Format = StringFormat.SingleLine };
        public static readonly StringField Title = new() { MaximumLength = 500 };
        public static readonly StringField Creator = new() { MaximumLength = 500 };
        public static readonly StringField Problem = new() { MaximumLength = 64, Format = StringFormat.SingleLine };
        public static readonly StringField ProblemDetail = new() { MaximumLength = 200, Format = StringFormat.SingleLine };
        public static readonly StringField Name = new() { MaximumLength = 200, Format = StringFormat.SingleLine };
        public static readonly StringField Source = new() { MaximumLength = 16, Format = StringFormat.SingleLine };
        public static readonly IntField Count = new() { Minimum = 0 };
        public static readonly LongArrayField WorkIds = new() { Minimum = 1, MaximumLength = MaximumItems };
    }
}
