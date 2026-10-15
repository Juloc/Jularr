using Jularr.Backend.Data.Enums.Playback;

namespace Jularr.Backend.Data.Logic.Playback
{
    internal sealed class PlaybackConversion
    {
        public PlaybackConversion(PlaybackModeType playbackModeTypeId, int? videoStreamIndex, int? audioStreamIndex, bool copyVideo, bool copyAudio)
        {
            PlaybackModeTypeId = playbackModeTypeId;
            VideoStreamIndex = videoStreamIndex;
            AudioStreamIndex = audioStreamIndex;
            CopyVideo = copyVideo;
            CopyAudio = copyAudio;
        }

        public PlaybackModeType PlaybackModeTypeId { get; }
        public int? VideoStreamIndex { get; }
        public int? AudioStreamIndex { get; }
        public bool CopyVideo { get; }
        public bool CopyAudio { get; }

        // Converted audio's rate in kilobits a second.
        public int? AudioKilobits { get; init; }
    }
}
