using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Playback;

namespace Jularr.Backend.Data.Logic.Playback
{
    // A marker set by hand on an episode's files.
    internal sealed class MediaMarkerSaveData : BaseData
    {
        public MediaMarkerSaveData(long workId, long workUnitId, MediaMarkerKind mediaMarkerKindTypeId, long startMs, long endMs)
        {
            WorkId = workId;
            WorkUnitId = workUnitId;
            MediaMarkerKindTypeId = mediaMarkerKindTypeId;
            StartMs = startMs;
            EndMs = endMs;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Id))]
        public long WorkUnitId { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.MediaMarkerKindTypeId))]
        public MediaMarkerKind MediaMarkerKindTypeId { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.StartMs))]
        public long StartMs { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.EndMs))]
        public long EndMs { get; }
    }

    internal sealed class MediaMarkerKeyData : BaseData
    {
        public MediaMarkerKeyData(long workId, long workUnitId, MediaMarkerKind mediaMarkerKindTypeId)
        {
            WorkId = workId;
            WorkUnitId = workUnitId;
            MediaMarkerKindTypeId = mediaMarkerKindTypeId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Id))]
        public long WorkUnitId { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.MediaMarkerKindTypeId))]
        public MediaMarkerKind MediaMarkerKindTypeId { get; }
    }

    // The marker of one kind goes to the season's other episodes, shifted by the offset.
    internal sealed class MediaMarkerCopyData : BaseData
    {
        public MediaMarkerCopyData(long workId, long workUnitId, MediaMarkerKind mediaMarkerKindTypeId, long offsetMs)
        {
            WorkId = workId;
            WorkUnitId = workUnitId;
            MediaMarkerKindTypeId = mediaMarkerKindTypeId;
            OffsetMs = offsetMs;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Id))]
        public long WorkUnitId { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.MediaMarkerKindTypeId))]
        public MediaMarkerKind MediaMarkerKindTypeId { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.OffsetMs))]
        public long OffsetMs { get; }
    }

    // The derived markers and chapters ready to be stored, as JSON.
    internal sealed class MediaFileMarkerRowsData : BaseData
    {
        public MediaFileMarkerRowsData(long storedFileId, string chapters, string markers)
        {
            StoredFileId = storedFileId;
            Chapters = chapters;
            Markers = markers;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.Payload))]
        public string Chapters { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.Payload))]
        public string Markers { get; }
    }
}
