using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Admin.Storage.V1
{
    public sealed class DirectCleanupStatsV1 : BaseResponse
    {
        public DirectCleanupStatsV1(long scanned, long deleted, long skipped, long errors, long reclaimedBytes)
        {
            Scanned = scanned;
            Deleted = deleted;
            Skipped = skipped;
            Errors = errors;
            ReclaimedBytes = reclaimedBytes;
        }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.CleanupCount))]
        public long Scanned { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.CleanupCount))]
        public long Deleted { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.CleanupCount))]
        public long Skipped { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.CleanupCount))]
        public long Errors { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.CleanupBytes))]
        public long ReclaimedBytes { get; }
    }
}
