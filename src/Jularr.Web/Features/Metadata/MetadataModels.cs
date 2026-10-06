namespace Jularr.Web.Features.Metadata;

public sealed class AnimeMetadata
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AnimeId { get; set; }
    public string Provider { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string PreferredTitle { get; set; } = "";
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public string? NativeTitle { get; set; }
    public string? Description { get; set; }
    public string? CoverImageUrl { get; set; }
    public string? BannerImageUrl { get; set; }
    public string? Format { get; set; }
    public string? Status { get; set; }
    public string? Season { get; set; }
    public int? SeasonYear { get; set; }
    public int? EpisodeCount { get; set; }
    public int? EpisodeDurationMinutes { get; set; }

    /// <summary>Provider average score on a 0-100 scale (AniList <c>averageScore</c>); null when unrated.</summary>
    public int? AverageScore { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed record AnimeMetadataCandidate(
    string Provider,
    string ExternalId,
    string PreferredTitle,
    string? RomajiTitle,
    string? EnglishTitle,
    string? NativeTitle,
    string? Description,
    string? CoverImageUrl,
    string? BannerImageUrl,
    string? Format,
    string? Status,
    string? Season,
    int? SeasonYear,
    int? EpisodeCount,
    int? EpisodeDurationMinutes,
    int? AverageScore = null,
    string? TrailerKey = null);

public sealed record AnimeMetadataMatchResult(
    bool Success,
    string? Error = null);

public sealed record AutomaticAnimeEpisodeMappingResult(
    bool Applied,
    string Reason,
    IReadOnlyList<AnimeEpisodeMetadataMapping> Mappings)
{
    public static AutomaticAnimeEpisodeMappingResult Skipped(string reason) =>
        new(false, reason, []);
}

public sealed record AniListAnimeRelation(
    string RelationType,
    AnimeMetadataCandidate Candidate);

/// <summary>Display fields of one AniList entry; <see cref="MediaType"/> is AniList's ANIME or MANGA.</summary>
public sealed record AniListMediaSummary(
    string MediaType,
    string ExternalId,
    string Title,
    string? NativeTitle,
    string? CoverImageUrl,
    string? Format,
    string? Status,
    int? Year);

public sealed record AniListMediaRelation(
    string RelationType,
    AniListMediaSummary Media);

/// <summary>One AniList entry and its related entries, from a single request.</summary>
public sealed record AniListRelatedMedia(
    AniListMediaSummary? Media,
    IReadOnlyList<AniListMediaRelation> Relations);


public interface IAnimeMetadataProvider
{
    string Key { get; }

    Task<IReadOnlyList<AnimeMetadataCandidate>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);

    Task<AnimeMetadataCandidate?> GetAsync(
        string externalId,
        CancellationToken cancellationToken);
}

public static class AnimeMetadataTitles
{
    public static string Choose(
        string? english,
        string? romaji,
        string? native,
        string fallback) =>
        FirstNonEmpty(english, romaji, native, fallback) ?? fallback;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}


public sealed record AnimeEpisodeMetadataMapping(
    Guid Id,
    Guid AnimeId,
    int SeasonNumber,
    int LocalEpisodeStart,
    int LocalEpisodeEnd,
    int RemoteEpisodeStart,
    string Provider,
    string ExternalId,
    string PreferredTitle,
    int? EpisodeCount,
    DateTimeOffset UpdatedAt)
{
    public bool Contains(int seasonNumber, int episodeNumber) =>
        SeasonNumber == seasonNumber &&
        episodeNumber >= LocalEpisodeStart &&
        episodeNumber <= LocalEpisodeEnd;

    public int ResolveRemoteEpisode(int localEpisodeNumber) =>
        RemoteEpisodeStart + (localEpisodeNumber - LocalEpisodeStart);
}

public sealed record ResolvedAnimeEpisodeMetadata(
    string Provider,
    string ExternalId,
    string PreferredTitle,
    int RemoteEpisodeNumber,
    int? EpisodeCount,
    bool IsExplicitRange);

public static class AnimeEpisodeMetadataRules
{
    public static bool Overlaps(
        AnimeEpisodeMetadataMapping mapping,
        int seasonNumber,
        int localEpisodeStart,
        int localEpisodeEnd) =>
        mapping.SeasonNumber == seasonNumber &&
        localEpisodeStart <= mapping.LocalEpisodeEnd &&
        localEpisodeEnd >= mapping.LocalEpisodeStart;

    public static int ResolveAutomaticLocalEnd(
        int localEpisodeStart,
        int localSeasonMaximum,
        int remoteEpisodeStart,
        int? remoteEpisodeCount)
    {
        if (localEpisodeStart <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(localEpisodeStart));
        }

        if (localSeasonMaximum < localEpisodeStart)
        {
            throw new ArgumentOutOfRangeException(nameof(localSeasonMaximum));
        }

        if (remoteEpisodeStart <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remoteEpisodeStart));
        }

        if (remoteEpisodeCount is not > 0)
        {
            return localEpisodeStart;
        }

        var availableRemoteEpisodes = remoteEpisodeCount.Value - remoteEpisodeStart + 1;
        if (availableRemoteEpisodes <= 0)
        {
            return localEpisodeStart;
        }

        return Math.Min(
            localSeasonMaximum,
            localEpisodeStart + availableRemoteEpisodes - 1);
    }
}
