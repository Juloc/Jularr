using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Logic.Instance
{
    // How a provider last answered, as far as Jularr has asked it.
    internal sealed record ProviderObservation(bool Succeeded, DateTime At);

    // The keys of the providers the check looks for.
    internal sealed class HealthProviderKeysData : BaseData
    {
        public HealthProviderKeysData(string[] providerKeys)
        {
            ProviderKeys = providerKeys;
        }

        [DataField(typeof(HealthFields), nameof(HealthFields.ProviderKeys))]
        public string[] ProviderKeys { get; }
    }

    // A metadata provider the health check may list: switched on, and with a stored key where one is needed.
    internal sealed class HealthProviderData : BaseData
    {
        public HealthProviderData(string providerKey, bool isEnabled, bool hasSecret)
        {
            ProviderKey = providerKey;
            IsEnabled = isEnabled;
            HasSecret = hasSecret;
        }

        [DataField(typeof(HealthFields), nameof(HealthFields.ProviderKey))]
        public string ProviderKey { get; }

        [DataField(typeof(HealthFields), nameof(HealthFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(HealthFields), nameof(HealthFields.HasSecret))]
        public bool HasSecret { get; }
    }

    // A storage connection with what its last look found; the storage worker keeps it current.
    internal sealed class HealthMountData : BaseData
    {
        public HealthMountData(long storageMountId, string mountName, StorageHealthType? storageHealthTypeId, string? diagnosticCode, DateTime? checkedAt)
        {
            StorageMountId = storageMountId;
            MountName = mountName;
            StorageHealthTypeId = storageHealthTypeId;
            DiagnosticCode = diagnosticCode;
            CheckedAt = checkedAt;
        }

        [DataField(typeof(HealthFields), nameof(HealthFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(HealthFields), nameof(HealthFields.MountName))]
        public string MountName { get; }

        [DataField(typeof(HealthFields), nameof(HealthFields.StorageHealthTypeId))]
        public StorageHealthType? StorageHealthTypeId { get; }

        [DataField(typeof(HealthFields), nameof(HealthFields.ErrorKey))]
        public string? DiagnosticCode { get; }

        [DataField(typeof(HealthFields), nameof(HealthFields.CheckedAt))]
        public DateTime? CheckedAt { get; }
    }

    // An indexer, download client or news server that is switched on, and what its last test said.
    internal sealed class HealthConnectionData : BaseData
    {
        public HealthConnectionData(long id, string connectionName, AcquisitionConnectionType connectionTypeId, long version, DateTime? checkedAt, string? lastErrorKey)
        {
            Id = id;
            ConnectionName = connectionName;
            ConnectionTypeId = connectionTypeId;
            Version = version;
            CheckedAt = checkedAt;
            LastErrorKey = lastErrorKey;
        }

        [DataField(typeof(HealthFields), nameof(HealthFields.Id))]
        public long Id { get; }

        [DataField(typeof(HealthFields), nameof(HealthFields.ConnectionName))]
        public string ConnectionName { get; }

        [DataField(typeof(HealthFields), nameof(HealthFields.ConnectionTypeId))]
        public AcquisitionConnectionType ConnectionTypeId { get; }

        [DataField(typeof(HealthFields), nameof(HealthFields.Version))]
        public long Version { get; }

        [DataField(typeof(HealthFields), nameof(HealthFields.CheckedAt))]
        public DateTime? CheckedAt { get; }

        [DataField(typeof(HealthFields), nameof(HealthFields.ErrorKey))]
        public string? LastErrorKey { get; }
    }
}
