using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Logic.Library
{
    internal sealed class LibraryRootCreationData : BaseData
    {
        public LibraryRootCreationData(string name, MediaType mediaTypeId, long storageMountId, string relativePath)
        {
            Name = name;
            MediaTypeId = mediaTypeId;
            StorageMountId = storageMountId;
            RelativePath = relativePath;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Name))]
        public string Name { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RelativePath { get; }
    }

    internal sealed class LibraryRootParameters : BaseData
    {
        public LibraryRootParameters(long libraryRootId, long? version = null)
        {
            LibraryRootId = libraryRootId;
            Version = version;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Version), false)]
        public long? Version { get; }
    }

    internal sealed class LibraryRootIdData : BaseData
    {
        public LibraryRootIdData(long id)
        {
            Id = id;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long Id { get; }
    }

    internal sealed class LibraryRootVersionData : BaseData
    {
        public LibraryRootVersionData(long version)
        {
            Version = version;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Version))]
        public long Version { get; }
    }

    internal sealed class LibraryRootLocationData : BaseData
    {
        public LibraryRootLocationData(long id, MediaType mediaTypeId, long storageMountId, StorageProtocolType storageProtocolTypeId, string sharePath, string relativePath)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            StorageMountId = storageMountId;
            StorageProtocolTypeId = storageProtocolTypeId;
            SharePath = sharePath;
            RelativePath = relativePath;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long Id { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.StorageProtocolTypeId))]
        public StorageProtocolType StorageProtocolTypeId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.SharePath))]
        public string SharePath { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RelativePath { get; }
    }

    internal sealed class LibraryRootRenameData : BaseData
    {
        public LibraryRootRenameData(long libraryRootId, string name, long version)
        {
            LibraryRootId = libraryRootId;
            Name = name;
            Version = version;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Name))]
        public string Name { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Version))]
        public long Version { get; }
    }

    internal sealed class LibraryRootFilterData : BaseData
    {
        public LibraryRootFilterData(long? storageMountId)
        {
            StorageMountId = storageMountId;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id), false)]
        public long? StorageMountId { get; }
    }
}
