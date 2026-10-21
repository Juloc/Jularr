using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Playback;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class WorkUnitMarkerParametersV1 : BaseParameters
    {
        public WorkUnitMarkerParametersV1(long workId, long workUnitId)
        {
            WorkId = workId;
            WorkUnitId = workUnitId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Id))]
        public long WorkUnitId { get; }
    }

    public sealed class WorkUnitMarkerKindParametersV1 : BaseParameters
    {
        public WorkUnitMarkerKindParametersV1(long workId, long workUnitId, MediaMarkerKind mediaMarkerKindTypeId)
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

    // One episode with its file's marker of one kind; an episode without markers appears once with none.
    public sealed class WorkMarkerRowV1 : BaseResponse
    {
        public WorkMarkerRowV1(long workUnitId, int seasonNumber, decimal number, string? title, long? storedFileId, long? durationMs, int chapterCount, MediaMarkerKind? mediaMarkerKindTypeId,
            MediaMarkerSource? mediaMarkerSourceTypeId, long? startMs, long? endMs)
        {
            WorkUnitId = workUnitId;
            SeasonNumber = seasonNumber;
            Number = number;
            Title = title;
            StoredFileId = storedFileId;
            DurationMs = durationMs;
            ChapterCount = chapterCount;
            MediaMarkerKindTypeId = mediaMarkerKindTypeId;
            MediaMarkerSourceTypeId = mediaMarkerSourceTypeId;
            StartMs = startMs;
            EndMs = endMs;
        }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Id))]
        public long WorkUnitId { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.SeasonNumber))]
        public int SeasonNumber { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber))]
        public decimal Number { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Title))]
        public string? Title { get; }

        // The file the markers belong to; none while the episode has no available file.
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id), false)]
        public long? StoredFileId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.DurationMs))]
        public long? DurationMs { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.Count))]
        public int ChapterCount { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.MediaMarkerKindTypeId), false)]
        public MediaMarkerKind? MediaMarkerKindTypeId { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.MediaMarkerSourceTypeId), false)]
        public MediaMarkerSource? MediaMarkerSourceTypeId { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.StartMs), false)]
        public long? StartMs { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.EndMs), false)]
        public long? EndMs { get; }
    }

    // A marker of one kind, set by hand on every available file of the episode.
    public sealed class SaveWorkMarkerDataV1 : BaseData
    {
        public SaveWorkMarkerDataV1(MediaMarkerKind mediaMarkerKindTypeId, decimal startSeconds, decimal endSeconds)
        {
            MediaMarkerKindTypeId = mediaMarkerKindTypeId;
            StartSeconds = startSeconds;
            EndSeconds = endSeconds;
        }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.MediaMarkerKindTypeId))]
        public MediaMarkerKind MediaMarkerKindTypeId { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.StartSeconds))]
        public decimal StartSeconds { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.EndSeconds))]
        public decimal EndSeconds { get; }
    }

    // The episode's marker of one kind goes to the other episodes of its season, shifted by the offset.
    public sealed class CopyWorkMarkersDataV1 : BaseData
    {
        public CopyWorkMarkersDataV1(MediaMarkerKind mediaMarkerKindTypeId, decimal offsetSeconds)
        {
            MediaMarkerKindTypeId = mediaMarkerKindTypeId;
            OffsetSeconds = offsetSeconds;
        }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.MediaMarkerKindTypeId))]
        public MediaMarkerKind MediaMarkerKindTypeId { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.OffsetSeconds))]
        public decimal OffsetSeconds { get; }
    }

    public sealed class CopyWorkMarkersResultV1 : BaseResponse
    {
        public CopyWorkMarkersResultV1(int copiedCount)
        {
            CopiedCount = copiedCount;
        }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.Count))]
        public int CopiedCount { get; }
    }
}
