using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Library;

namespace Jularr.Backend.Data.Admin.Library.V1
{
    public sealed class DeleteLibraryFilesParametersV1 : BaseParameters
    {
    }

    public sealed class DeleteLibraryFilesDataV1 : BaseData
    {
        public DeleteLibraryFilesDataV1(long[] storedFileIds)
        {
            StoredFileIds = storedFileIds;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Ids))]
        public long[] StoredFileIds { get; }
    }

    // Files that could not be removed, on an unreachable library or with an unusable recycle folder, stay where they are.
    public sealed class DeleteLibraryFilesResultV1 : BaseResponse
    {
        public DeleteLibraryFilesResultV1(int deletedCount, int keptCount)
        {
            DeletedCount = deletedCount;
            KeptCount = keptCount;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.DeletedCount))]
        public int DeletedCount { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.KeptCount))]
        public int KeptCount { get; }
    }
}
