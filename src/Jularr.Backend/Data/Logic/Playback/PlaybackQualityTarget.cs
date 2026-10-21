namespace Jularr.Backend.Data.Logic.Playback
{
    // What a conversion may deliver at most; no value is no cap. A limit of the instance lowers a target with LoweredBy.
    internal sealed record PlaybackQualityTarget(int? MaxHeight, int? VideoKilobits, int? AudioKilobits)
    {
        public static PlaybackQualityTarget Uncapped { get; } = new(null, null, null);

        public PlaybackQualityTarget LoweredBy(PlaybackQualityTarget limit) =>
            new(Lower(MaxHeight, limit.MaxHeight), Lower(VideoKilobits, limit.VideoKilobits), Lower(AudioKilobits, limit.AudioKilobits));

        private static int? Lower(int? first, int? second) => first is { } a && second is { } b ? Math.Min(a, b) : first ?? second;
    }
}
