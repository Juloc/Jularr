using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Fields.Library;
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

    public sealed class PlaybackSegmentParametersV1 : BaseParameters
    {
        public PlaybackSegmentParametersV1(Guid playbackSessionId, int index)
        {
            PlaybackSessionId = playbackSessionId;
            Index = index;
        }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PlaybackSessionId))]
        public Guid PlaybackSessionId { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.SegmentIndex))]
        public int Index { get; }
    }

    // An audio or subtitle choice; image subtitles are listed but not yet supported.
    public sealed class PlaybackTrackV1 : BaseResponse
    {
        public PlaybackTrackV1(string key, MediaTrackType mediaTrackTypeId, string? language, string? title, int? channels, bool isDefault, bool isForced, bool isSupported)
        {
            Key = key;
            MediaTrackTypeId = mediaTrackTypeId;
            Language = language;
            Title = title;
            Channels = channels;
            IsDefault = isDefault;
            IsForced = isForced;
            IsSupported = isSupported;
        }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.TrackKey))]
        public string Key { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.MediaTrackTypeId))]
        public MediaTrackType MediaTrackTypeId { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Language))]
        public string? Language { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Title))]
        public string? Title { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Channels))]
        public int? Channels { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.IsDefault))]
        public bool IsDefault { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.IsForced))]
        public bool IsForced { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.IsSupported))]
        public bool IsSupported { get; }
    }

    public sealed class PlaybackSubtitleParametersV1 : BaseParameters
    {
        public PlaybackSubtitleParametersV1(long mediaAssetId, string key)
        {
            MediaAssetId = mediaAssetId;
            Key = key;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.TrackKey))]
        public string Key { get; }
    }

    public sealed class PlaybackSubtitleV1 : BaseResponse, ITextOutput
    {
        public PlaybackSubtitleV1(string text)
        {
            Text = text;
        }

        [JsonIgnore]
        public string Text { get; }

        [JsonIgnore]
        public string ContentType => "text/vtt; charset=utf-8";
    }

    public sealed class PlaybackPlaylistV1 : BaseResponse, ITextOutput
    {
        public PlaybackPlaylistV1(string text)
        {
            Text = text;
        }

        [JsonIgnore]
        public string Text { get; }

        [JsonIgnore]
        public string ContentType => "application/vnd.apple.mpegurl";
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
        public StartPlaybackDataV1(string[] containers, string[] videoCodecs, string[] audioCodecs, int? audioStreamIndex = null)
        {
            Containers = containers;
            VideoCodecs = videoCodecs;
            AudioCodecs = audioCodecs;
            AudioStreamIndex = audioStreamIndex;
        }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.Formats))]
        public string[] Containers { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.Formats))]
        public string[] VideoCodecs { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.Formats))]
        public string[] AudioCodecs { get; }

        // An audio track other than the file's default is delivered through a conversion.
        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.StreamIndex), false)]
        public int? AudioStreamIndex { get; }

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
