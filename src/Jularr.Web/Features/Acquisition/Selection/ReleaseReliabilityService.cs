using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Operations;

namespace Jularr.Web.Features.Acquisition.Selection;

/// <summary>
/// What recent downloads say about indexers and release groups, as the selection engine reads it: per source and per group, how many downloads
/// the client finished and how many it reported failed. A group with enough samples speaks for itself, otherwise its indexer does; with too few
/// samples nothing is known and nothing is held against a source. The points it yields are bounded (<see cref="ReleaseReliability"/>) and only
/// break a late tie, so it can never override identity, a gate or quality.
/// </summary>
public sealed class ReleaseReliabilityLookup(IReadOnlyDictionary<string, ReleaseReliability> bySource, IReadOnlyDictionary<string, ReleaseReliability> byGroup)
{
    public static ReleaseReliabilityLookup Empty { get; } = new(new Dictionary<string, ReleaseReliability>(), new Dictionary<string, ReleaseReliability>());

    public ReleaseReliability? For(string? source, string? group)
    {
        if (group is { Length: > 0 } && byGroup.TryGetValue(Key(group), out var known) && known.Samples >= ReleaseReliability.MinimumSamples)
        {
            return known;
        }

        return source is { Length: > 0 } && bySource.TryGetValue(Key(source), out var indexer) ? indexer : null;
    }

    internal static string Key(string value) => value.Trim().ToLowerInvariant();
}

/// <summary>
/// Builds the <see cref="ReleaseReliabilityLookup"/> from the Operations that already record every download, so there is no second store of
/// outcomes. Only what the download client itself reported counts: a finished job is a success and a failed one (missing articles, a failed
/// repair or unpack) is a failure of that release. A submission that never reached the client, a cancelled or interrupted job, a full disk or an
/// import problem are not recorded as download outcomes and so are never held against a source. Only the most recent
/// <see cref="MaxDownloads"/> downloads of the last <see cref="Window"/> count, so a source that improved is judged by how it does now.
/// </summary>
public sealed class ReleaseReliabilityService(AppDbContext db, TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(90);
    public const int MaxDownloads = 500;

    public async Task<ReleaseReliabilityLookup> LoadAsync(CancellationToken cancellationToken)
    {
        var outcomes = await new OperationStore(db).ListRecentDownloadOutcomesAsync(clock.GetUtcNow().UtcDateTime - Window, MaxDownloads, cancellationToken);
        var bySource = new Dictionary<string, (int Samples, int Successes)>();
        var byGroup = new Dictionary<string, (int Samples, int Successes)>();
        foreach (var (status, json) in outcomes)
        {
            if (!DownloadOperationDetails.TryParse(json, out var details) || details is null)
            {
                continue;
            }

            // A failure counts only when it was the release's fault; a full disk, a lost job or an unclassified failure says nothing about the source.
            if (status == OperationStatus.Failed && !SabnzbdFailureKinds.IsReleaseFault(details.FailureKind))
            {
                continue;
            }

            var success = status == OperationStatus.Succeeded ? 1 : 0;
            Count(bySource, details.ReleaseSource, success);
            Count(byGroup, details.ReleaseGroup, success);
        }

        return new ReleaseReliabilityLookup(ToRecords(bySource), ToRecords(byGroup));
    }

    private static void Count(Dictionary<string, (int Samples, int Successes)> counts, string? name, int success)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var key = ReleaseReliabilityLookup.Key(name);
        var (samples, successes) = counts.GetValueOrDefault(key);
        counts[key] = (samples + 1, successes + success);
    }

    private static Dictionary<string, ReleaseReliability> ToRecords(Dictionary<string, (int Samples, int Successes)> counts) =>
        counts.ToDictionary(pair => pair.Key, pair => new ReleaseReliability(pair.Value.Samples, pair.Value.Successes));
}
