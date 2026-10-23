using Jularr.Backend.Data.Enums.Field;
using Jularr.Backend.Data.Enums.Playback;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Playback
{
    public static class PlaybackPreferenceFields
    {
        public static readonly StringField Language = new(NpgsqlDbType.Varchar) { MaximumLength = 2, Required = false, Format = StringFormat.Language };
        public static readonly EnumField SubtitleModeTypeId = new(typeof(SubtitleMode));
        public static readonly DecimalField Speed = new() { Minimum = 0.25m, Maximum = 4m, Scale = 2 };
        public static readonly BoolField AutoplayNext = new();
        public static readonly EnumField SkipModeTypeId = new(typeof(SkipMode));
        public static readonly BoolField AutoplayPreviews = new();
    }
}
