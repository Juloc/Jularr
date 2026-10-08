using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;

namespace Jularr.Web.Features.Acquisition.Wanted;

/// <summary>
/// When each media type's upgrade scan last ran in this process and where the next one continues; a scan is cheap to repeat after a restart,
/// so nothing is persisted.
/// </summary>
public sealed class UpgradeScanState
{
    /// <summary>How often the held titles of a media type are looked at for upgrades; an idle library costs one query per interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<MediaAcquisitionKind, DateTime> next = new();
    private readonly ConcurrentDictionary<MediaAcquisitionKind, Guid> cursors = new();
    private readonly ConcurrentDictionary<MediaAcquisitionKind, DateTime> profiles = new();

    /// <summary>The last title the previous scan looked at (empty before the first one), so a library larger than one scan is walked in turns instead of its first titles every hour.</summary>
    public Guid CursorOf(MediaAcquisitionKind kind) => cursors.GetValueOrDefault(kind);

    /// <summary>Continues after <paramref name="last"/> next time (at the next pass, as the library is not finished), or from the start when the scan reached the end of the library.</summary>
    public void Continue(MediaAcquisitionKind kind, Guid last, bool reachedEnd)
    {
        cursors[kind] = reachedEnd ? Guid.Empty : last;
        if (!reachedEnd)
        {
            next[kind] = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Claims the scan of <paramref name="kind"/> when it is due and schedules the next one. Profiles that changed since the previous claim
    /// (<paramref name="profilesChangedAt"/>) make it due at once and start the library over, because what is upgradable may have changed for any title.
    /// </summary>
    public bool TryStart(MediaAcquisitionKind kind, DateTime nowUtc, TimeSpan interval, DateTime profilesChangedAt = default)
    {
        var profilesChanged = profiles.TryGetValue(kind, out var seen) && seen != profilesChangedAt;
        profiles[kind] = profilesChangedAt;
        if (profilesChanged)
        {
            cursors[kind] = Guid.Empty;
        }
        else if (next.TryGetValue(kind, out var due) && due > nowUtc)
        {
            return false;
        }

        next[kind] = nowUtc + interval;
        return true;
    }
}

/// <summary>
/// The upgrade part of the shared Wanted pass: a bounded page of the titles whose library holds something Monitoring or a request wants is
/// reconciled (an installed target whose profile wants a better version joins the queue, one that is final leaves it), and a title with queued targets
/// whose request ended Completed continues that request, so its tried releases stay remembered and the same lifecycle searches, grabs and imports the
/// better release. At most once per <see cref="UpgradeScanState.Interval"/>.
/// </summary>
public sealed class UpgradeWantedSource(MediaAcquisitionKind kind, WantedReconciler wanted, AcquisitionAccessStore requests, QualityProfileStore profiles, UpgradeScanState scans) : IWantedSource
{
    public const int MaxTitlesPerPass = 200;

    public MediaAcquisitionKind Kind => kind;

    public async Task<int> PrepareAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (!scans.TryStart(kind, nowUtc, UpgradeScanState.Interval, profiles.ChangedAtUtc()))
        {
            return 0;
        }

        var page = await wanted.ReconcileUpgradesAsync(kind, scans.CursorOf(kind), MaxTitlesPerPass, cancellationToken);
        scans.Continue(kind, page.Works.Count == 0 ? Guid.Empty : page.Works[^1], page.ReachedEnd);
        var reopened = 0;
        foreach (var workId in page.Works)
        {
            if (await wanted.AnyAsync(workId, cancellationToken) && await wanted.CompletedRequestOfAsync(kind, workId, cancellationToken) is { } requestId && await ReopenAsync(Guid.Parse(requestId), cancellationToken))
            {
                reopened++;
            }
        }

        return reopened;
    }

    // One conditional write: the search state and the status move together, and only while the request is still Completed.
    private async Task<bool> ReopenAsync(Guid requestId, CancellationToken cancellationToken) =>
        await requests.PatchPayloadAsync(
            requestId,
            ResetSearch,
            AcquisitionRequestStatus.Completed,
            _ => new AcquisitionStatusOutcome(AcquisitionRequestStatus.Approved, "A better release is wanted for the installed quality. Searching.", ClearOperation: true),
            cancellationToken);

    // Every release payload keeps its search state under the same names; the tried releases stay.
    private static string? ResetSearch(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return stored;
        }

        try
        {
            if (JsonNode.Parse(stored) is not JsonObject payload)
            {
                return stored;
            }

            payload["searches"] = 0;
            payload["nextSearchUtc"] = null;
            payload["lastProblem"] = null;
            return payload.ToJsonString(JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return stored;
        }
    }
}
