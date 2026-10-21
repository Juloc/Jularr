using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Fields.Playback;

namespace Jularr.Backend.Data.User.Playback.V1
{
    // A stretch of the played file the viewer may skip.
    public sealed class PlaybackMarkerV1 : BaseResponse
    {
        public PlaybackMarkerV1(MediaMarkerKind mediaMarkerKindTypeId, long startMs, long endMs)
        {
            MediaMarkerKindTypeId = mediaMarkerKindTypeId;
            StartMs = startMs;
            EndMs = endMs;
        }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.MediaMarkerKindTypeId))]
        public MediaMarkerKind MediaMarkerKindTypeId { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.StartMs))]
        public long StartMs { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.EndMs))]
        public long EndMs { get; }
    }

    // One chapter of the played file; a chapter without a title is named by its number.
    public sealed class PlaybackChapterV1 : BaseResponse
    {
        public PlaybackChapterV1(int chapterIndex, long startMs, long endMs, string? title)
        {
            ChapterIndex = chapterIndex;
            StartMs = startMs;
            EndMs = endMs;
            Title = title;
        }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.ChapterIndex))]
        public int ChapterIndex { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.StartMs))]
        public long StartMs { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.StartMs))]
        public long EndMs { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.Title))]
        public string? Title { get; }
    }
}
