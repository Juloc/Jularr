namespace Jularr.Integrations.Data.AniBridge
{
    // One release of the AniBridge mappings (anibridge/anibridge-mappings, MIT): where AniList's anime entries lie at TheTVDB.
    internal sealed class AniBridgeRelease(string? etag, string schemaVersion, DateTimeOffset? generatedAt, IReadOnlyList<AniBridgeMapping> mappings)
    {
        // For the next conditional download.
        public string? ETag { get; } = etag;
        public string SchemaVersion { get; } = schemaVersion;
        public DateTimeOffset? GeneratedAt { get; } = generatedAt;
        public IReadOnlyList<AniBridgeMapping> Mappings { get; } = mappings;
    }

    // An AniList entry in one season of a TheTVDB series: its first episode there and the season's episodes it covers (no last: open-ended).
    internal sealed class AniBridgeMapping(int aniListId, int tvdbId, int seasonNumber, int aniListEpisode, int firstEpisode, int? lastEpisode)
    {
        public int AniListId { get; } = aniListId;
        public int TvdbId { get; } = tvdbId;
        public int SeasonNumber { get; } = seasonNumber;
        public int AniListEpisode { get; } = aniListEpisode;
        public int FirstEpisode { get; } = firstEpisode;
        public int? LastEpisode { get; } = lastEpisode;
    }
}
