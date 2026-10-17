using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Library;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed record ArtworkScanCandidate(string RelativePath, string Folder, short SlotId, int? SeasonNumber, long? SizeBytes = null, DateTime? ModifiedAt = null);

    internal sealed class ArtworkScanBatchData : BaseData
    {
        public ArtworkScanBatchData(long libraryRootId, DateTime seenAt, string candidates)
        {
            LibraryRootId = libraryRootId;
            SeenAt = seenAt;
            Candidates = candidates;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.LastSeenAt))]
        public DateTime SeenAt { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Payload))]
        public string Candidates { get; }
    }
}
