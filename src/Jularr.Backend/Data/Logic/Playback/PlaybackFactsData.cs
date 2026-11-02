using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Playback;

namespace Jularr.Backend.Data.Logic.Playback
{
    // The file a playback session plays, as the probe read it.
    internal sealed class PlaybackFactsFileData : BaseData
    {
        public PlaybackFactsFileData(long storedFileId, string? container, long? bitRate, PlaybackModeType playbackModeTypeId)
        {
            StoredFileId = storedFileId;
            Container = container;
            BitRate = bitRate;
            PlaybackModeTypeId = playbackModeTypeId;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Container))]
        public string? Container { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.BitRate))]
        public long? BitRate { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PlaybackModeTypeId))]
        public PlaybackModeType PlaybackModeTypeId { get; }
    }

    // A stream of that file with what the probe knows of it.
    internal sealed class PlaybackFactsTrackData : BaseData
    {
        public PlaybackFactsTrackData(int streamIndex, MediaTrackType mediaTrackTypeId, bool isDefault, string? codec, string? language, int? width, int? height, int? channels, int? bitDepth,
            string? dynamicRange)
        {
            StreamIndex = streamIndex;
            MediaTrackTypeId = mediaTrackTypeId;
            IsDefault = isDefault;
            Codec = codec;
            Language = language;
            Width = width;
            Height = height;
            Channels = channels;
            BitDepth = bitDepth;
            DynamicRange = dynamicRange;
        }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.StreamIndex))]
        public int StreamIndex { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.MediaTrackTypeId))]
        public MediaTrackType MediaTrackTypeId { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.IsDefault))]
        public bool IsDefault { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Codec))]
        public string? Codec { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Language))]
        public string? Language { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Width))]
        public int? Width { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Height))]
        public int? Height { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Channels))]
        public int? Channels { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.BitDepth))]
        public int? BitDepth { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.DynamicRange))]
        public string? DynamicRange { get; }
    }
}
