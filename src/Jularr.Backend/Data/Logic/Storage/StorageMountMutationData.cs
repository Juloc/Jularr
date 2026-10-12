using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Logic.Storage
{
    internal sealed class StorageMountVersionData : BaseData
    {
        public StorageMountVersionData(long version)
        {
            Version = version;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Version))]
        public long Version { get; }
    }

    internal sealed class StorageMountSaveData : BaseData
    {
        public StorageMountSaveData(long? id, string name, StorageProtocolType storageProtocolTypeId, string? host, string sharePath, string? protocolVersion, string? username, string? domain,
            bool isReadOnly, byte[]? protectedPassword, string? wakeMacAddress, string? wakeBroadcastAddress, bool wakeOnDemand)
        {
            Id = id;
            Name = name;
            StorageProtocolTypeId = storageProtocolTypeId;
            Host = host;
            SharePath = sharePath;
            ProtocolVersion = protocolVersion;
            Username = username;
            Domain = domain;
            IsReadOnly = isReadOnly;
            ProtectedPassword = protectedPassword;
            WakeMacAddress = wakeMacAddress;
            WakeBroadcastAddress = wakeBroadcastAddress;
            WakeOnDemand = wakeOnDemand;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id), false)]
        public long? Id { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Name))]
        public string Name { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.StorageProtocolTypeId))]
        public StorageProtocolType StorageProtocolTypeId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Host))]
        public string? Host { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.SharePath))]
        public string SharePath { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.ProtocolVersion))]
        public string? ProtocolVersion { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Username))]
        public string? Username { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Domain))]
        public string? Domain { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.IsReadOnly))]
        public bool IsReadOnly { get; }

        [JsonIgnore]
        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.ProtectedPassword))]
        public byte[]? ProtectedPassword { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.WakeMacAddress))]
        public string? WakeMacAddress { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.WakeBroadcastAddress))]
        public string? WakeBroadcastAddress { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.WakeOnDemand))]
        public bool WakeOnDemand { get; }
    }

    internal sealed class StorageMountParameters : BaseData
    {
        public StorageMountParameters(long storageMountId, long? version = null)
        {
            StorageMountId = storageMountId;
            Version = version;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Version), false)]
        public long? Version { get; }
    }

    internal sealed class StorageMountIdData : BaseData
    {
        public StorageMountIdData(long id)
        {
            Id = id;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long Id { get; }
    }
}
