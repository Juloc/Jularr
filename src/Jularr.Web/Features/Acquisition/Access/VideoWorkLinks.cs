using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Providers;
using Jularr.Web.Features.Operations;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>
/// The canonical addresses of a Movie or Series Work. Every Admin and notification link to Movie/TV media goes
/// through here, so a link is always built from the Work id and never from a title, a search query or a path.
/// </summary>
public static class VideoWorkLinks
{
    /// <summary>The consumer detail page of the Work.</summary>
    public static string DetailPath(MediaAcquisitionKind kind, Guid workId) => $"/Library/{ConsumerSegment(kind)}/{workId:D}";

    /// <summary>The Admin media page of the Work (monitoring, files, acquisition).</summary>
    public static string AdminPath(MediaAcquisitionKind kind, Guid workId) => $"/Admin/Media/{AdminSegment(kind)}/{workId:D}";

    /// <summary>The download-operation target key of a whole Work (Movie); <see cref="VideoRequestWorkResolver"/> reads it back.</summary>
    public static string WorkTarget(Guid workId) => $"{WorkTargetPrefix}{workId:D}";

    /// <summary>The download-operation target key of one episode.</summary>
    public static string EpisodeTarget(Guid episodeId) => $"{EpisodeTargetPrefix}{episodeId:D}";

    public const string WorkTargetPrefix = "work:";
    public const string EpisodeTargetPrefix = "work-episode:";

    public static bool TryParseAdminKind(string? segment, out MediaAcquisitionKind kind)
    {
        kind = segment switch
        {
            "movie" => MediaAcquisitionKind.Movie,
            "series" => MediaAcquisitionKind.Tv,
            _ => MediaAcquisitionKind.Anime
        };
        return kind != MediaAcquisitionKind.Anime;
    }

    public static WorkMediaType WorkType(MediaAcquisitionKind kind) => kind switch
    {
        MediaAcquisitionKind.Movie => WorkMediaType.Movie,
        MediaAcquisitionKind.Tv => WorkMediaType.Series,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only Movie and TV are Work-keyed video media.")
    };

    public static MediaAcquisitionKind AcquisitionKind(WorkMediaType type) => type switch
    {
        WorkMediaType.Movie => MediaAcquisitionKind.Movie,
        WorkMediaType.Series => MediaAcquisitionKind.Tv,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Only Movie and Series Works have video acquisition.")
    };

    private static string ConsumerSegment(MediaAcquisitionKind kind) => kind switch
    {
        MediaAcquisitionKind.Movie => "Movie",
        MediaAcquisitionKind.Tv => "Series",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only Movie and TV have a Work detail page.")
    };

    private static string AdminSegment(MediaAcquisitionKind kind) => kind switch
    {
        MediaAcquisitionKind.Movie => "movie",
        MediaAcquisitionKind.Tv => "series",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only Movie and TV have a Work admin page.")
    };
}

/// <summary>The canonical Work a Movie or TV request names.</summary>
/// <param name="ExternalIds">The confirmed TMDB/TVDB/IMDb ids of the Work by lowercase provider key, the structured evidence a search may send to indexers that support it.</param>
public sealed record VideoRequestWork(Guid WorkId, string Title, int? Year, IReadOnlyDictionary<string, string>? ExternalIds = null);

/// <summary>
/// Finds the canonical Work of Movie and TV requests. A request names its title by provider identity; the engine
/// resolves the same identity when it searches, so this is the read-side twin of that lookup. One query covers
/// any number of requests; a request whose Work does not exist (yet) is simply absent from the result.
/// </summary>
public sealed class VideoRequestWorkResolver(AppDbContext db)
{
    /// <summary>The Work per request id, for the Movie and TV requests among <paramref name="requests"/>.</summary>
    public async Task<IReadOnlyDictionary<Guid, VideoRequestWork>> ResolveAsync(IEnumerable<AcquisitionRequest> requests, CancellationToken cancellationToken)
    {
        var video = requests.Where(request => request.Kind is MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv).ToArray();
        if (video.Length == 0)
        {
            return new Dictionary<Guid, VideoRequestWork>();
        }

        var providers = video.Select(request => Provider(request)).Distinct().ToArray();
        var externalIds = video.Select(request => request.ExternalId.Trim()).Distinct().ToArray();
        var identities = await (
                from identity in db.WorkExternalIdentities.AsNoTracking()
                join work in db.Works.AsNoTracking() on identity.WorkId equals work.Id
                where identity.MediaType == work.MediaType
                      && (work.MediaType == WorkMediaType.Movie || work.MediaType == WorkMediaType.Series)
                      && providers.Contains(identity.Provider)
                      && externalIds.Contains(identity.ExternalId)
                select new { work.MediaType, identity.Provider, identity.ExternalId, WorkId = work.Id, Title = work.CanonicalTitle, work.Year })
            .ToListAsync(cancellationToken);

        var matched = video
            .Select(request => (Request: request, Match: identities.FirstOrDefault(identity => identity.MediaType == VideoWorkLinks.WorkType(request.Kind) && identity.Provider == Provider(request) && identity.ExternalId == request.ExternalId.Trim())))
            .Where(pair => pair.Match is not null)
            .ToArray();
        var matchedWorkIds = matched.Select(pair => pair.Match!.WorkId).Distinct().ToArray();
        string[] searchProviders = [ProviderKeys.Tmdb, ProviderKeys.Tvdb, ProviderKeys.Imdb];
        var evidence = matchedWorkIds.Length == 0
            ? []
            : await (
                    from identity in db.WorkExternalIdentities.AsNoTracking()
                    join work in db.Works.AsNoTracking() on identity.WorkId equals work.Id
                    where matchedWorkIds.Contains(work.Id)
                          && identity.MediaType == work.MediaType
                          && identity.ReviewState == MappingReviewState.Confirmed
                          && searchProviders.Contains(identity.Provider)
                    select new { identity.WorkId, identity.Provider, identity.ExternalId })
                .ToListAsync(cancellationToken);

        var works = new Dictionary<Guid, VideoRequestWork>();
        foreach (var (request, match) in matched)
        {
            var ids = evidence.Where(item => item.WorkId == match!.WorkId)
                .GroupBy(item => item.Provider)
                .ToDictionary(group => group.Key, group => group.First().ExternalId);
            works[request.Id] = new VideoRequestWork(match!.WorkId, match.Title, match.Year, ids);
        }

        return works;
    }

    /// <summary>Season numbers selected by TV requests, resolved only against their own canonical Work.</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<int>>> ResolveSeasonNumbersAsync(IEnumerable<AcquisitionRequest> requests, CancellationToken cancellationToken)
    {
        var selected = requests
            .Where(request => request.Kind == MediaAcquisitionKind.Tv)
            .Select(request => (RequestId: request.Id, Payload: VideoRequestPayload.Parse(request.PayloadJson)))
            .Where(item => item.Payload is { Scope: VideoRequestScope.Custom })
            .Select(item => (item.RequestId, Payload: item.Payload!))
            .ToArray();
        if (selected.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<int>>();
        }

        var seasonIds = selected.SelectMany(item => item.Payload.SelectedSeasonIds ?? []).Distinct().ToArray();
        var episodeIds = selected.SelectMany(item => item.Payload.SelectedEpisodeIds).Distinct().ToArray();
        var seasons = seasonIds.Length == 0
            ? []
            : await db.WorkSeasons.AsNoTracking().Where(season => seasonIds.Contains(season.Id)).Select(season => new { season.Id, season.WorkId, season.SeasonNumber }).ToArrayAsync(cancellationToken);
        var episodes = episodeIds.Length == 0
            ? []
            : await db.WorkEpisodes.AsNoTracking().Where(episode => episodeIds.Contains(episode.Id)).Select(episode => new { episode.Id, episode.WorkId, episode.SeasonNumber }).ToArrayAsync(cancellationToken);

        return selected.ToDictionary(
            item => item.RequestId,
            item => (IReadOnlyList<int>)seasons.Where(season => season.WorkId == item.Payload.WorkId && (item.Payload.SelectedSeasonIds ?? []).Contains(season.Id)).Select(season => season.SeasonNumber)
                .Concat(episodes.Where(episode => episode.WorkId == item.Payload.WorkId && item.Payload.SelectedEpisodeIds.Contains(episode.Id)).Select(episode => episode.SeasonNumber))
                .Distinct()
                .Order()
                .ToArray());
    }

    /// <summary>
    /// The Admin media address of the Movie or Series a Movie/TV download operation belongs to, by operation id. The
    /// video engine stores the target as <c>work:{id}</c> or <c>work-episode:{id}</c>; episodes resolve to their Series in one query.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, string>> ResolveOperationLinksAsync(IEnumerable<OperationSnapshot> operations, CancellationToken cancellationToken)
    {
        var targets = new List<(Guid OperationId, MediaAcquisitionKind Kind, bool IsEpisode, Guid Id)>();
        foreach (var operation in operations)
        {
            if (!DownloadOperationDetails.TryParse(operation.Details, out var details)
                || details!.MediaKind is not (MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv)
                || details.TargetKey is not { } key)
            {
                continue;
            }

            var isEpisode = key.StartsWith(VideoWorkLinks.EpisodeTargetPrefix, StringComparison.Ordinal);
            if ((isEpisode || key.StartsWith(VideoWorkLinks.WorkTargetPrefix, StringComparison.Ordinal))
                && Guid.TryParse(key[(isEpisode ? VideoWorkLinks.EpisodeTargetPrefix : VideoWorkLinks.WorkTargetPrefix).Length..], out var id))
            {
                targets.Add((operation.Id, details.MediaKind, isEpisode, id));
            }
        }

        var episodeIds = targets.Where(target => target.IsEpisode).Select(target => target.Id).Distinct().ToArray();
        var episodeWorks = episodeIds.Length == 0
            ? []
            : await db.WorkEpisodes.AsNoTracking().Where(episode => episodeIds.Contains(episode.Id)).ToDictionaryAsync(episode => episode.Id, episode => episode.WorkId, cancellationToken);
        var workIds = targets.Where(target => !target.IsEpisode).Select(target => target.Id).Distinct().ToArray();
        var existing = workIds.Length == 0
            ? []
            : (await db.Works.AsNoTracking().Where(work => workIds.Contains(work.Id)).Select(work => work.Id).ToListAsync(cancellationToken)).ToHashSet();
        var links = new Dictionary<Guid, string>();
        foreach (var target in targets)
        {
            if (!target.IsEpisode)
            {
                if (!existing.Contains(target.Id))
                {
                    continue;
                }

                links[target.OperationId] = VideoWorkLinks.AdminPath(target.Kind, target.Id);
            }
            else if (episodeWorks.TryGetValue(target.Id, out var workId))
            {
                links[target.OperationId] = VideoWorkLinks.AdminPath(target.Kind, workId);
            }
        }

        return links;
    }

    private static string Provider(AcquisitionRequest request) => request.Provider.Trim().ToLowerInvariant();
}
