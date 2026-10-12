using Jularr.Integrations.Data.Newznab;

namespace Jularr.Backend.Logic.Acquisition
{
    // How a Work is searched; Anime and Audiobook are acquisition kinds of the shared media types.
    internal enum AcquisitionKind : byte
    {
        Movie = 1,
        Tv = 2,
        Anime = 3,
        Book = 4,
        Audiobook = 5,
        Manga = 6,
        LightNovel = 7,
        Music = 8,
        Game = 9
    }

    // A localizable explanation: a stable code and an optional data value.
    internal sealed record ReleaseReason(string Code, string? Value = null);

    // How far one search goes; Deep is explicit troubleshooting, never a background default.
    internal enum SearchDepth : byte
    {
        Fast = 1,
        Normal = 2,
        Deep = 3
    }

    // The canonical target of one search, independent of any indexer.
    internal sealed record SearchIntent(AcquisitionKind Kind, string Title)
    {
        public IReadOnlyList<string> Aliases { get; init; } = [];
        public int? Year { get; init; }
        public string? Creator { get; init; }
        public IReadOnlyDictionary<string, string> ExternalIds { get; init; } = new Dictionary<string, string>();
        public int? Season { get; init; }
        public int? Episode { get; init; }
        public int? AbsoluteEpisode { get; init; }
        public int? Volume { get; init; }
        public decimal? Chapter { get; init; }
    }

    // Explicit bounds of one search pass, so a slow indexer only costs its own share.
    internal sealed record SearchBudget(int MaxConcurrentIndexers, TimeSpan IndexerTimeout, int MaxQueryVariants, int MaxPages, int MaxResults, int TargetUsable, int MaxTier)
    {
        public static SearchBudget For(SearchDepth depth) => depth switch
        {
            SearchDepth.Fast => new(4, TimeSpan.FromSeconds(12), 2, 1, 300, 1, 0),
            SearchDepth.Deep => new(4, TimeSpan.FromSeconds(45), 24, 4, 1200, int.MaxValue, 2),
            _ => new(4, TimeSpan.FromSeconds(25), 8, 2, 600, 3, 1)
        };
    }

    // One query of a plan: its rung in the ladder and what it asks for.
    internal sealed record PlannedQuery(string Stage, int Tier, NewznabSearchMode Mode, string? Text, IReadOnlyList<KeyValuePair<string, string>> Parameters, string Provenance, bool AnyCategory = false)
    {
        public string Key => $"{Mode}|{Text?.ToLowerInvariant()}|{string.Join('&', Parameters.Select(pair => $"{pair.Key}={pair.Value}"))}|{(AnyCategory ? "any" : "cat")}";
    }

    // One normalized release found by an indexer; the download link never leaves the server.
    internal sealed record ReleaseOffer(string Title, long ConnectionId, string ConnectionName, int ConnectionPriority, string? Guid, Uri? DownloadUri, string? InfoUrl, long? SizeBytes,
        DateTimeOffset? PublishedAt, IReadOnlyList<string> Stages)
    {
        public string Identity => ConnectionId + ":" + (Guid ?? Title);
    }

    // What a media type concluded about one release.
    internal sealed record ReleaseJudgement<TMatch>(TMatch Match, ReleaseInfo? Parsed, ReleaseIdentityEvidence Evidence, SelectionCoverage Coverage, string? SafetyRejection)
    {
        public int ContextScore { get; init; }
        public IReadOnlyList<string>? Languages { get; init; }
    }

    // One canonical episode a video search targets; movies have none.
    internal sealed record VideoUnit(long Id, int SeasonNumber, int EpisodeNumber, bool HasFile);

    internal enum VideoIdentityMatch : byte
    {
        Matches = 1,
        ContainsTarget = 2,
        WrongTitle = 3,
        WrongYear = 4,
        AmbiguousTitle = 5,
        WrongSeason = 6,
        WrongEpisode = 7,
        Unparseable = 8,
        NoDownload = 9
    }
}
