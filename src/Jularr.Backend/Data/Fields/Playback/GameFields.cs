using Jularr.Backend.Data.Enums.Playback;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Playback
{
    public static class GameFields
    {
        public static readonly EnumField GameSaveTypeId = new(typeof(GameSaveType));
        public static readonly ByteArrayField Data = new() { MinimumLength = 1, MaximumLength = 16 * 1024 * 1024 };
        public static readonly DateTimeField UpdatedAt = new();
        public static readonly StringField FileName = new(NpgsqlDbType.Varchar) { MaximumLength = 255, AllowBlank = false };
        public static readonly StringField Emulator = new(NpgsqlDbType.Varchar) { MaximumLength = 32, Required = false };
        public static readonly LongField PlayedMs = new() { Minimum = 0 };
        public static readonly IntField PlayedSeconds = new() { Minimum = 1, Maximum = 300 };
        public static readonly IntArrayField EmulatedPlatforms = new() { Minimum = 1 };
        public static readonly BoolField IsEmulated = new();
    }
}
