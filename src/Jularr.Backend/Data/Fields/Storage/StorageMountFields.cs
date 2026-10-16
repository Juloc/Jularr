using Jularr.Backend.Data.Enums.Field;
using Jularr.Backend.Data.Enums.Storage;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Storage
{
    public static class StorageMountFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 128, AllowBlank = false, Trim = true };
        public static readonly EnumField StorageProtocolTypeId = new(typeof(StorageProtocolType));
        public static readonly StringField Host = new(NpgsqlDbType.Varchar) { MaximumLength = 253, AllowBlank = false, Required = false, Format = StringFormat.HostName };
        public static readonly StringField SharePath = new(NpgsqlDbType.Varchar) { MaximumLength = 1024, AllowBlank = false, Format = StringFormat.SingleLine };
        public static readonly StringField ExportPath = new(NpgsqlDbType.Varchar) { MaximumLength = 1024, AllowBlank = false, Format = StringFormat.AbsolutePath };
        public static readonly StringField ProtocolVersion = new(NpgsqlDbType.Varchar) { MaximumLength = 8, AllowBlank = false, Required = false };
        public static readonly StringField Username = new(NpgsqlDbType.Varchar) { MaximumLength = 256, Required = false, Format = StringFormat.SingleLine };
        public static readonly StringField Domain = new(NpgsqlDbType.Varchar) { MaximumLength = 256, Required = false, Format = StringFormat.SingleLine };
        public static readonly StringField Password = new() { MaximumLength = 1024, Required = false, Format = StringFormat.SingleLine };
        public static readonly ByteArrayField ProtectedPassword = new() { Required = false };
        public static readonly BoolField HasPassword = new();
        public static readonly BoolField IsReadOnly = new();
        public static readonly BoolField IsVolume = new();
        public static readonly StringField WakeMacAddress = new(NpgsqlDbType.Varchar) { MaximumLength = 17, Required = false, Trim = true, Format = StringFormat.MacAddress };
        public static readonly StringField WakeBroadcastAddress = new(NpgsqlDbType.Varchar) { MaximumLength = 15, Required = false, Trim = true, Format = StringFormat.IPv4Address };
        public static readonly BoolField WakeOnDemand = new();
        public static readonly BoolField IsNetwork = new();
        public static readonly StringField Compose = new() { MaximumLength = 4096 };
        public static readonly StringField LocalPath = new() { MaximumLength = 1024, AllowBlank = false, Format = StringFormat.SingleLine };
        public static readonly StringField DirectoryName = new() { MaximumLength = 255, AllowBlank = false };
        public static readonly LongField Version = new() { Minimum = 1 };
        public static readonly DateTimeField CreatedAt = new();
        public static readonly DateTimeField UpdatedAt = new();
    }
}
