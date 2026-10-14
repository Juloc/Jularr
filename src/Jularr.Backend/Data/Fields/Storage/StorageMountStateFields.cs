using Jularr.Backend.Data.Enums.Storage;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Storage
{
    public static class StorageMountStateFields
    {
        public static readonly EnumField StorageAvailabilityTypeId = new(typeof(StorageAvailabilityType));
        public static readonly EnumField StorageHealthTypeId = new(typeof(StorageHealthType)) { Required = false };
        public static readonly StringField DiagnosticCode = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly DateTimeField CheckedAt = new() { Required = false };
        public static readonly LongField FreeBytes = new() { Minimum = 0, Required = false };
        public static readonly LongField TotalBytes = new() { Minimum = 0, Required = false };
        public static readonly BoolField IsConnectRequested = new();
        public static readonly IntField ReconnectAttempts = new() { Minimum = 0 };
        public static readonly IntField DelayMinutes = new() { Minimum = 1, Maximum = 60 };
    }
}
