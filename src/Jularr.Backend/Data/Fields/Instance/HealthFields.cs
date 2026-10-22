using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Storage;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Instance
{
    public static class HealthFields
    {
        public static readonly EnumField ServiceTypeId = new(typeof(HealthServiceType));
        public static readonly EnumField StateTypeId = new(typeof(HealthStateType));

        // A key of the service: what it is and, for several of a kind, which one.
        public static readonly StringField Key = new() { MaximumLength = 100, AllowBlank = false };
        public static readonly StringField Name = new() { MaximumLength = 200, Required = false };
        public static readonly StringField ErrorKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly DateTimeField CheckedAt = new() { Required = false };
        public static readonly BoolField IsStable = new();

        // What the check reads of the configuration.
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly LongField Version = new() { Minimum = 1 };
        public static readonly BoolField IsEnabled = new();
        public static readonly BoolField HasSecret = new();
        public static readonly StringField ProviderKey = new() { MaximumLength = 100, AllowBlank = false };
        public static readonly StringArrayField ProviderKeys = new() { ItemMaximumLength = 100 };
        public static readonly StringField MountName = new(NpgsqlDbType.Varchar) { MaximumLength = 128, AllowBlank = false };
        public static readonly StringField ConnectionName = new(NpgsqlDbType.Varchar) { MaximumLength = 128, AllowBlank = false };
        public static readonly EnumField StorageHealthTypeId = new(typeof(StorageHealthType)) { Required = false };
        public static readonly EnumField ConnectionTypeId = new(typeof(AcquisitionConnectionType));
    }
}
