using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;

namespace Jularr.Web.Features.Acquisition.Pipeline;

/// <summary>
/// What the Wanted queue and the open request of an anime say about its episodes, read once for the admin detail and the release calendar: which episodes are
/// wanted, which are on their way (the episodes of the grab in flight), and whether the search for the wanted ones came back empty.
/// </summary>
public sealed class AnimeEpisodeStates(AppDbContext db, AcquisitionAccessStore requests)
{
    private static readonly AcquisitionRequestStatus[] OpenStatuses = [AcquisitionRequestStatus.Approved, AcquisitionRequestStatus.Searching, AcquisitionRequestStatus.Downloading, AcquisitionRequestStatus.Importing];

    public async Task<AnimeEpisodeStateMap> LoadAsync(string? animeKey, CancellationToken cancellationToken)
    {
        var payloads = new Dictionary<string, AnimeRequestPayload>(StringComparer.OrdinalIgnoreCase);
        foreach (var status in OpenStatuses)
        {
            foreach (var request in await requests.ListByStatusAsync(MediaAcquisitionKind.Anime, status, cancellationToken))
            {
                if (AnimeRequestPayload.Of(request) is { AnimeKey: { Length: > 0 } key } payload && (animeKey is null || key.Equals(animeKey, StringComparison.OrdinalIgnoreCase)))
                {
                    payloads[key] = payload;
                }
            }
        }

        return new AnimeEpisodeStateMap(await AnimeCanonicalEpisodes.WantedAsync(db, animeKey, cancellationToken), payloads);
    }
}

public sealed class AnimeEpisodeStateMap(IReadOnlyList<AnimeWantedEpisode> wanted, IReadOnlyDictionary<string, AnimeRequestPayload> payloads)
{
    private readonly Dictionary<(string Anime, int Season, int Episode), AnimeWantedEpisode> byKey = wanted
        .GroupBy(item => Slot(item.Key))
        .ToDictionary(group => group.Key, group => group.First());

    public static AnimeEpisodeStateMap Empty { get; } = new([], new Dictionary<string, AnimeRequestPayload>());

    public IReadOnlyList<AnimeWantedEpisode> Wanted => wanted;

    public AnimeWantedEpisode? WantedOf(AnimeEpisodeKey key) => byKey.GetValueOrDefault(Slot(key));

    /// <summary>Grabbed while the episode is part of the grab in flight; Failed while it is wanted and the request's last search found nothing.</summary>
    public AcquisitionAttemptStatus? AttemptOf(AnimeEpisodeKey key)
    {
        if (!payloads.TryGetValue(key.AnimeKey, out var payload))
        {
            return null;
        }

        if (payload.Episodes?.Any(episode => Slot(episode) == Slot(key)) == true)
        {
            return AcquisitionAttemptStatus.Grabbed;
        }

        return SearchedInVain(key, payload) ? AcquisitionAttemptStatus.Failed : null;
    }

    /// <summary>How many searches for the wanted episode found nothing so far.</summary>
    public int FailuresOf(AnimeEpisodeKey key) =>
        payloads.TryGetValue(key.AnimeKey, out var payload) && SearchedInVain(key, payload) ? payload.Searches : 0;

    private bool SearchedInVain(AnimeEpisodeKey key, AnimeRequestPayload payload) =>
        payload is { Searches: > 0, NextSearchUtc: not null } && WantedOf(key) is not null;

    private static (string, int, int) Slot(AnimeEpisodeKey key) => (key.AnimeKey.ToLowerInvariant(), key.SeasonNumber, key.EpisodeNumber);
}
