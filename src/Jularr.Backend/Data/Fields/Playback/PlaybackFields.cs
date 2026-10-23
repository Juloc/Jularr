using Jularr.Backend.Data.Enums.Playback;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Playback
{
    public static class PlaybackFields
    {
        public static readonly GuidField PlaybackSessionId = new();
        public static readonly EnumField PlaybackModeTypeId = new(typeof(PlaybackModeType));
        public static readonly EnumField ProgressPositionTypeId = new(typeof(ProgressPositionType));
        public static readonly LongField PositionMs = new() { Minimum = 0, Maximum = 864_000_000 };
        public static readonly LongField DurationMs = new() { Minimum = 1, Maximum = 864_000_000, Required = false };
        public static readonly IntField SegmentIndex = new() { Minimum = 0, Maximum = 1_000_000 };
        public static readonly BoolField IsEnded = new();
        public static readonly BoolField IsCompleted = new();
        public static readonly StringArrayField Formats = new() { ItemMaximumLength = 32 };
        public static readonly StringField StreamUrl = new(NpgsqlDbType.Text) { MaximumLength = 256, Required = false };
        public static readonly StringField TrackKey = new(NpgsqlDbType.Varchar) { MaximumLength = 24, AllowBlank = false };
        public static readonly BoolField IsSupported = new();
        public static readonly BoolField IsSelected = new();
        public static readonly BoolField IsReduced = new();
    }
}
