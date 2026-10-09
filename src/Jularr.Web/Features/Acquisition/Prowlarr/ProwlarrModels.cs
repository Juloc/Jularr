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

public sealed class ProwlarrException(string message, Exception? innerException = null)
    : Exception(message, innerException);
