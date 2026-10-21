using Jularr.Backend.Data.Enums.Playback;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Playback
{
    public static class TranscodeFields
    {
        public static readonly EnumField TranscodeAccelerationTypeId = new(typeof(TranscodeAcceleration));
        public static readonly EnumField BestTranscodeAccelerationTypeId = new(typeof(TranscodeAcceleration)) { Required = false };
        public static readonly IntField MaxTranscodes = new() { Minimum = 1, Maximum = 64 };
        public static readonly IntField RecommendedMaxTranscodes = new() { Minimum = 1, Maximum = 64, Required = false };
        public static readonly IntArrayField LadderHeights = new() { Minimum = 240, Maximum = 4320, MaximumLength = 6, IsSet = true };
        public static readonly LongField Version = new() { Minimum = 1 };
        public static readonly DateTimeField RanAt = new() { Required = false };
        public static readonly IntField CpuCores = new() { Minimum = 1, Maximum = 4096, Required = false };
        public static readonly IntField Height = new() { Minimum = 1, Maximum = 8640 };
        public static readonly DecimalField SpeedFactor = new() { Minimum = 0, Maximum = 999999, Scale = 2, Required = false };
        public static readonly StringField Encoder = new(NpgsqlDbType.Varchar) { MaximumLength = 64, AllowBlank = false };
        public static readonly StringField Device = new(NpgsqlDbType.Varchar) { MaximumLength = 256, Required = false };
        public static readonly BoolField IsAvailable = new();
        public static readonly BoolField IsRunning = new();
        public static readonly StringField Results = new() { MaximumLength = 65536, AllowBlank = false };
    }
}
