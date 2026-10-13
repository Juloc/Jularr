namespace Jularr.Integrations.Data.MusicBrainz
{
    internal enum MusicBrainzFailure : byte
    {
        Unavailable,
        NotFound,
        RateLimited,
        InvalidResponse
    }

    internal sealed class MusicBrainzException : Exception
    {
        public MusicBrainzException(MusicBrainzFailure failure, string message, Exception? innerException = null) : base(message, innerException)
        {
            Failure = failure;
        }

        public MusicBrainzFailure Failure { get; }
    }

    // A release group is the album a listener means, across its editions.
    internal sealed class MusicBrainzAlbum
    {
        public MusicBrainzAlbum(string id, string title, string artist, int? year, string? primaryType)
        {
            Id = id;
            Title = title;
            Artist = artist;
            Year = year;
            PrimaryType = primaryType;
        }

        public string Id { get; }
        public string Title { get; }
        public string Artist { get; }
        public int? Year { get; }
        public string? PrimaryType { get; }
    }

    internal sealed class MusicBrainzTrack
    {
        public MusicBrainzTrack(int number, string title)
        {
            Number = number;
            Title = title;
        }

        // Position across all discs of the edition.
        public int Number { get; }
        public string Title { get; }
    }

    internal sealed class MusicBrainzAlbumDetails
    {
        public MusicBrainzAlbumDetails(MusicBrainzAlbum album, IReadOnlyList<MusicBrainzTrack> tracks)
        {
            Album = album;
            Tracks = tracks;
        }

        public MusicBrainzAlbum Album { get; }
        public IReadOnlyList<MusicBrainzTrack> Tracks { get; }
    }
}
