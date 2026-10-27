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
        // The signed, time-limited address a cast device plays from (it cannot sign in).
        public static readonly StringField CastToken = new(NpgsqlDbType.Varchar) { MaximumLength = 1024, AllowBlank = false };
        public static readonly StringField CastUrl = new(NpgsqlDbType.Text) { MaximumLength = 1200 };
        public static readonly StringField TrackKey = new(NpgsqlDbType.Varchar) { MaximumLength = 24, AllowBlank = false };
        public static readonly BoolField IsSupported = new();
        public static readonly BoolField IsSelected = new();
        public static readonly BoolField IsReduced = new();
        // The audio runs later (positive) or earlier (negative) than the picture by this many milliseconds.
        public static readonly IntField AudioOffsetMs = new() { Minimum = -10_000, Maximum = 10_000 };
        // The Info pane: what a conversion does with the video and the audio.
        public static readonly BoolField CopyVideo = new() { Required = false };
        public static readonly BoolField CopyAudio = new() { Required = false };
        public static readonly IntField TargetHeight = new() { Minimum = 1, Maximum = 100_000, Required = false };
        public static readonly IntField Kilobits = new() { Minimum = 1, Maximum = 10_000_000, Required = false };
        public static readonly StringField Encoder = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
    }
}
