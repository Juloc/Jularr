using System.Text.Json.Serialization;
using Jularr.Web.Features.Acquisition;

using Jularr.Web.Features.Acquisition.Search;

namespace Jularr.Web.Features.Acquisition.Prowlarr;

public sealed record ProwlarrSettings(
    string BaseUrl,
    int[] Categories,
    int[] IndexerIds,
    int SearchLimit)
{
    public static ProwlarrSettings CreateDefault(string baseUrl) =>
        new(
            baseUrl,
            Categories: [5000, 5070],
            IndexerIds: [],
            SearchLimit: 100);
}

public sealed record ProwlarrConnection(
    ProwlarrSettings Settings,
    [property: JsonIgnore] string ApiKey);

public sealed record ProwlarrConnectionTestResult(
    bool Success,
    string? Version = null,
    string? Error = null);

public enum ProwlarrAnimeSearchMode
{
    Anime,
    Episode,
    Season
}

public sealed record ProwlarrAnimeSearchTarget(
    string CanonicalTitle,
    IReadOnlyList<string> Aliases,
    ProwlarrAnimeSearchMode Mode,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    int? AbsoluteEpisodeNumber = null);

public sealed record ProwlarrSearchQuery(string Query, int Offset = 0, int? Limit = null);

public sealed record ProwlarrSearchWarning(
    string Query,
    string Message);

public sealed record ProwlarrReleaseCandidate(
    string Title,
    string? Indexer,
    int? IndexerId,
    string? Protocol,
    long? SizeBytes,
    int? Seeders,
    int? Leechers,
    DateTimeOffset? PublishedAt,
    int? AgeDays,
    double? AgeHours,
    string? Guid,
    string? InfoUrl,
    AnimeReleaseInfo ParsedRelease,
    IReadOnlyList<string> MatchedQueries,
    [property: JsonIgnore] Uri? InternalDownloadUri,
    [property: JsonIgnore] string? InternalMagnetUri)
{
    public string Identity =>
        !string.IsNullOrWhiteSpace(Guid)
            ? $"prowlarr:{IndexerId?.ToString() ?? "unknown"}:{Guid}"
            : $"release:{IndexerId?.ToString() ?? "unknown"}:{ParsedRelease.ReleaseKey}";

    /// <summary>
    /// Every indexer that returned this release, best source first. A search merges equivalent releases of several indexers into one
    /// logical candidate; this candidate's own fields are those of the first source.
    /// </summary>
    public IReadOnlyList<ReleaseSourceOption> Sources { get; init; } = [];

    /// <summary>The queries that found the release, per indexer, for the explanation Manual Search shows.</summary>
    public IReadOnlyList<QueryProvenance> Provenance { get; init; } = [];
}

public sealed class ProwlarrException(string message, Exception? innerException = null)
    : Exception(message, innerException);
