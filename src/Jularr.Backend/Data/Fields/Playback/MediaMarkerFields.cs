using Jularr.Backend.Data.Enums.Field;
using Jularr.Backend.Data.Enums.Playback;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Playback
{
    public static class MediaMarkerFields
    {
        public static readonly EnumField MediaMarkerKindTypeId = new(typeof(MediaMarkerKind));
        public static readonly EnumField MediaMarkerSourceTypeId = new(typeof(MediaMarkerSource));
        public static readonly LongField StartMs = new() { Minimum = 0 };
        public static readonly LongField EndMs = new() { Minimum = 1 };
        public static readonly LongField OffsetMs = new() { Minimum = -3_600_000, Maximum = 3_600_000 };

        // A person sets markers in seconds; the database keeps milliseconds.
        public static readonly DecimalField StartSeconds = new() { Minimum = 0, Maximum = 86400, Scale = 1 };
        public static readonly DecimalField EndSeconds = new() { Minimum = 0.1m, Maximum = 86400, Scale = 1 };
        public static readonly DecimalField OffsetSeconds = new() { Minimum = -3600, Maximum = 3600, Scale = 1 };

        public static readonly IntField ChapterIndex = new() { Minimum = 0 };
        public static readonly StringField Title = new(NpgsqlDbType.Varchar) { MaximumLength = 256, Required = false, Trim = true };
        public static readonly IntField Count = new() { Minimum = 0 };
        public static readonly StringField Payload = new() { AllowBlank = false };
    }
}
