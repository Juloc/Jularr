using Jularr.Backend.Data.Enums.Library;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Library
{
    public static class MediaTrackFields
    {
        public static readonly IntField StreamIndex = new() { Minimum = 0 };
        public static readonly EnumField MediaTrackTypeId = new(typeof(MediaTrackType));
        public static readonly StringField Codec = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly StringField Language = new(NpgsqlDbType.Varchar) { MaximumLength = 16, Required = false };
        public static readonly StringField Title = new(NpgsqlDbType.Varchar) { MaximumLength = 256, Required = false };
        public static readonly IntField Width = new() { Minimum = 1, Required = false };
        public static readonly IntField Height = new() { Minimum = 1, Required = false };
        public static readonly IntField Channels = new() { Minimum = 1, Required = false };
        public static readonly IntField BitDepth = new() { Minimum = 1, Maximum = 32, Required = false };
        public static readonly StringField DynamicRange = new(NpgsqlDbType.Varchar) { MaximumLength = 16, Required = false };
        public static readonly BoolField Is3D = new();
        public static readonly BoolField IsDefault = new();
        public static readonly BoolField IsForced = new();
        public static readonly StringArrayField Languages = new() { ItemMaximumLength = 16 };
    }
}
