using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Playback;

namespace Jularr.Backend.Data.User.Playback.V1
{
    public sealed class MediaAssetParametersV1 : BaseParameters
    {
        public MediaAssetParametersV1(long mediaAssetId)
        {
            MediaAssetId = mediaAssetId;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }
    }

    public sealed class PlaybackSessionParametersV1 : BaseParameters
    {
        public PlaybackSessionParametersV1(Guid playbackSessionId)
        {
            PlaybackSessionId = playbackSessionId;
        }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PlaybackSessionId))]
        public Guid PlaybackSessionId { get; }
    }

    public sealed class PlaybackItemV1 : BaseResponse
    {
        public PlaybackItemV1(long mediaAssetId, long workId, string canonicalTitle, MediaAssetType mediaAssetTypeId, WorkUnitType? workUnitTypeId, decimal? unitNumber,
            decimal? parentUnitNumber, long? resumePositionMs)
        {
            MediaAssetId = mediaAssetId;
            WorkId = workId;
            CanonicalTitle = canonicalTitle;
            MediaAssetTypeId = mediaAssetTypeId;
            WorkUnitTypeId = workUnitTypeId;
            UnitNumber = unitNumber;
            ParentUnitNumber = parentUnitNumber;
            ResumePositionMs = resumePositionMs;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.MediaAssetTypeId))]
        public MediaAssetType MediaAssetTypeId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitTypeId), false)]
        public WorkUnitType? WorkUnitTypeId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? UnitNumber { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? ParentUnitNumber { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PositionMs), false)]
        public long? ResumePositionMs { get; }
    }

    public sealed class StartPlaybackDataV1 : BaseData
    {
        public StartPlaybackDataV1(string[] containers, string[] videoCodecs, string[] audioCodecs)
        {
            Containers = containers;
            VideoCodecs = videoCodecs;
            AudioCodecs = audioCodecs;
        }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.Formats))]
        public string[] Containers { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.Formats))]
        public string[] VideoCodecs { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.Formats))]
        public string[] AudioCodecs { get; }

        public override void Validate()
        {
            base.Validate();

            // Capability documents stay small.
            if (Containers.Length > 32 || VideoCodecs.Length > 32 || AudioCodecs.Length > 32 || Containers.Concat(VideoCodecs).Concat(AudioCodecs).Any(item => item.Length is 0 or > 32))
            {
                throw new DataValidationException(nameof(Containers), "out_of_range");
            }
        }
    }

    public sealed class PlaybackPlanV1 : BaseResponse
    {
        public PlaybackPlanV1(Guid playbackSessionId, PlaybackModeType playbackModeTypeId, string streamUrl, long resumePositionMs, long? durationMs)
        {
            PlaybackSessionId = playbackSessionId;
            PlaybackModeTypeId = playbackModeTypeId;
            StreamUrl = streamUrl;
            ResumePositionMs = resumePositionMs;
            DurationMs = durationMs;
        }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PlaybackSessionId))]
        public Guid PlaybackSessionId { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PlaybackModeTypeId))]
        public PlaybackModeType PlaybackModeTypeId { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.StreamUrl), true)]
        public string StreamUrl { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PositionMs))]
        public long ResumePositionMs { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.DurationMs))]
        public long? DurationMs { get; }
    }

    public sealed class UpdatePlaybackDataV1 : BaseData
    {
        public UpdatePlaybackDataV1(long positionMs, long? durationMs, bool isEnded, bool isCompleted)
        {
            PositionMs = positionMs;
            DurationMs = durationMs;
            IsEnded = isEnded;
            IsCompleted = isCompleted;
        }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PositionMs))]
        public long PositionMs { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.DurationMs))]
        public long? DurationMs { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.IsEnded))]
        public bool IsEnded { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.IsCompleted))]
        public bool IsCompleted { get; }

        public override void Validate()
        {
            base.Validate();

            // Completion is an explicit end of playback.
            if (IsCompleted && !IsEnded)
            {
                throw new DataValidationException(nameof(IsCompleted), "invalid_transition");
            }
        }
    }

    public sealed class PlaybackStreamV1 : BaseResponse, IFileOutput
    {
        public PlaybackStreamV1(string filePath, string contentType)
        {
            FilePath = filePath;
            ContentType = contentType;
        }

        [JsonIgnore]
        public string FilePath { get; }

        [JsonIgnore]
        public string ContentType { get; }
    }
}
