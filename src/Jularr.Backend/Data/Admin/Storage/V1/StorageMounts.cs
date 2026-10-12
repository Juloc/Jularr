using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Admin.Storage.V1
{
    public sealed class ReadStorageMountsParametersV1 : BaseParameters
    {
        public ReadStorageMountsParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class CreateStorageMountParametersV1 : BaseParameters
    {
    }

    public sealed class ReadStorageProtocolsParametersV1 : BaseParameters
    {
    }

    public sealed class StorageProtocolVersionDetailsV1 : BaseResponse
    {
        public StorageProtocolVersionDetailsV1(StorageProtocolType storageProtocolTypeId, string protocolVersion)
        {
            StorageProtocolTypeId = storageProtocolTypeId;
            ProtocolVersion = protocolVersion;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.StorageProtocolTypeId))]
        public StorageProtocolType StorageProtocolTypeId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.ProtocolVersion))]
        public string ProtocolVersion { get; }
    }

    public sealed class StorageMountParametersV1 : BaseParameters
    {
        public StorageMountParametersV1(long storageMountId)
        {
            StorageMountId = storageMountId;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }
    }

    public sealed class StorageMountDetailsV1 : BaseResponse
    {
        public StorageMountDetailsV1(long id, string name, StorageProtocolType storageProtocolTypeId, string? host, string sharePath, string? protocolVersion, string? username, string? domain, bool isReadOnly,
            bool hasPassword, string? wakeMacAddress, string? wakeBroadcastAddress, bool wakeOnDemand, bool canMount, long version)
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
            HasPassword = hasPassword;
            WakeMacAddress = wakeMacAddress;
            WakeBroadcastAddress = wakeBroadcastAddress;
            WakeOnDemand = wakeOnDemand;
            CanMount = canMount;
            Version = version;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long Id { get; }

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

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.HasPassword))]
        public bool HasPassword { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.WakeMacAddress))]
        public string? WakeMacAddress { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.WakeBroadcastAddress))]
        public string? WakeBroadcastAddress { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.WakeOnDemand))]
        public bool WakeOnDemand { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.CanMount))]
        public bool CanMount { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Version))]
        public long Version { get; }
    }

    public sealed class SaveStorageMountDataV1 : BaseData
    {
        public SaveStorageMountDataV1(string name, StorageProtocolType storageProtocolTypeId, string? host, string sharePath, string? protocolVersion, string? username, string? domain, bool isReadOnly, string? password,
            string? wakeMacAddress = null, string? wakeBroadcastAddress = null, bool wakeOnDemand = false)
        {
            Name = name;
            StorageProtocolTypeId = storageProtocolTypeId;
            Host = host;
            SharePath = sharePath;
            ProtocolVersion = protocolVersion;
            Username = username;
            Domain = domain;
            IsReadOnly = isReadOnly;
            Password = password;
            WakeMacAddress = wakeMacAddress;
            WakeBroadcastAddress = wakeBroadcastAddress;
            WakeOnDemand = wakeOnDemand;
        }

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

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Password))]
        public string? Password { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.WakeMacAddress))]
        public string? WakeMacAddress { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.WakeBroadcastAddress))]
        public string? WakeBroadcastAddress { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.WakeOnDemand))]
        public bool WakeOnDemand { get; }

        public override void Validate()
        {
            base.Validate();

            // Match protocol configuration.
            var supported = StorageProtocolTypeId switch
            {
                StorageProtocolType.Smb => ProtocolVersion is "3.1.1" or "3.0",
                StorageProtocolType.Nfs => ProtocolVersion is "4.1" or "4.2",
                _ => ProtocolVersion is null
            };
            if (!supported)
            {
                throw new DataValidationException(nameof(ProtocolVersion), "unknown_catalog_value");
            }

            if ((StorageProtocolTypeId == StorageProtocolType.Local) != (Host is null))
            {
                throw new DataValidationException(nameof(Host), StorageProtocolTypeId == StorageProtocolType.Local ? "invalid_transition" : "required");
            }

            if (WakeMacAddress is null && (WakeBroadcastAddress is not null || WakeOnDemand))
            {
                throw new DataValidationException(nameof(WakeMacAddress), "required");
            }

            if (StorageProtocolTypeId == StorageProtocolType.Smb && (string.IsNullOrWhiteSpace(Username) || SharePath == "/"))
            {
                throw new DataValidationException(string.IsNullOrWhiteSpace(Username) ? nameof(Username) : nameof(SharePath), "required");
            }

            if (StorageProtocolTypeId != StorageProtocolType.Smb && (Username is not null || Domain is not null || Password is not null))
            {
                throw new DataValidationException(nameof(Username), "invalid_transition");
            }
        }
    }
    public sealed class CreateStorageMountResultV1 : BaseResponse
    {
        public CreateStorageMountResultV1(long id)
        {
            Id = id;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long Id { get; }
    }

    public sealed class StorageMountStatusV1 : BaseResponse
    {
        public StorageMountStatusV1(bool isMounted, bool isCurrentConfiguration)
        {
            IsMounted = isMounted;
            IsCurrentConfiguration = isCurrentConfiguration;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.IsMounted))]
        public bool IsMounted { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.IsCurrentConfiguration))]
        public bool IsCurrentConfiguration { get; }
    }

    public sealed class ReadStorageLocalPathsParametersV1 : BaseParameters
    {
    }

    public sealed class StorageLocalPathV1 : BaseResponse
    {
        public StorageLocalPathV1(string localPath)
        {
            LocalPath = localPath;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.LocalPath))]
        public string LocalPath { get; }
    }

    public sealed class ReadStorageDirectoriesDataV1 : BaseData
    {
        public ReadStorageDirectoriesDataV1(string relativePath)
        {
            RelativePath = relativePath;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RelativePath { get; }
    }

    public sealed class StorageDirectoryV1 : BaseResponse
    {
        public StorageDirectoryV1(string name)
        {
            Name = name;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.DirectoryName))]
        public string Name { get; }
    }
}
