namespace Jularr.Integrations.Data.Media
{
    // What an audio file's tags say about it; the MusicBrainz release group as Picard and Lidarr write it.
    internal sealed class AudioTags
    {
        public AudioTags(string? title, int? disc, int? track, string? releaseGroupId)
        {
            Title = title;
            Disc = disc;
            Track = track;
            ReleaseGroupId = releaseGroupId;
        }

        public string? Title { get; }
        public int? Disc { get; }
        public int? Track { get; }
        public string? ReleaseGroupId { get; }
    }

    // A Chromaprint fingerprint and the length it was taken from.
    internal sealed class AudioFingerprint
    {
        public AudioFingerprint(int durationSeconds, string value)
        {
            DurationSeconds = durationSeconds;
            Value = value;
        }

        public int DurationSeconds { get; }
        public string Value { get; }
    }
}
