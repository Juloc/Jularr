using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;

namespace Jularr.Web.Features.Acquisition.Wanted;

/// <summary>
/// The Usenet search state every release-backed request payload carries (Books, Manga, Light
/// Novels): the releases already sent to the download client (never sent twice), how many
/// searches ran, when to search again while no release exists and why the previous release did
/// not work out. Media payloads derive from it and only add what they search for; the JSON keeps
/// these fields flat next to the media fields.
/// </summary>
public abstract record ReleaseRequestPayload
{
    public IReadOnlyList<string>? TriedReleases { get; init; }

    public int Searches { get; init; }

    public DateTime? NextSearchUtc { get; init; }

    public string? LastProblem { get; init; }

    /// <summary>
    /// Merges this payload (what a search computed from the request it read) into the payload stored now, so a field that another
    /// owner changed while the search ran is not written back stale. The default has no such fields and keeps this payload.
    /// </summary>
    public virtual ReleaseRequestPayload Reconcile(string? storedJson) => this;

    /// <summary>Serializes the whole payload (media fields included) for the request store.</summary>
    public string Serialize() =>
        JsonSerializer.Serialize(this, GetType(), JsonSerializerOptions.Web);
}

/// <summary>A release a media search accepted, in ranked order.</summary>
/// <param name="Source">The indexer that returned the release.</param>
/// <param name="ReleaseGroup">The release group its name states; with the source it is what a download outcome is counted for.</param>
public sealed record ReleaseRequestCandidate(
    string Identity,
    string Title,
    Uri? DownloadUri,
    string? Source = null,
    string? ReleaseGroup = null);

/// <summary>What the download client said about one submitted release.</summary>
public sealed record ReleaseRequestSubmission(
    bool Accepted,
    Guid? OperationId,
    string Message)
{
    /// <summary>Set when a direct source imported the candidate on the spot: the request ends there instead of waiting for a download.</summary>
    public AcquisitionExecution? Completed { get; init; }
}

/// <summary>
/// The one release-request lifecycle shared by every media type that searches Usenet for a
/// request: pick the best untried release, remember it, submit it, and otherwise back off
/// (6 h, 12 h, then daily) until <see cref="MaxSearches"/> searches found nothing. The previous
/// problem is shown once in the next status message and then consumed, so repeated searches
/// never stack the same text.
/// </summary>
public sealed class ReleaseRequestTracker(
    AcquisitionAccessStore requests,
    TimeProvider clock)
{
    /// <summary>After this many searches without a usable release the request fails and waits for the owner.</summary>
    public const int MaxSearches = 12;

    public const string EveryReleaseTried = "Every matching release was tried already.";

    /// <summary>How long to wait before searching again after no indexer could answer at all.</summary>
    public static readonly TimeSpan UnavailableRetry = TimeSpan.FromMinutes(30);

    /// <summary>Wait before the next search while no release exists: 6 h, 12 h, then daily.</summary>
    public static TimeSpan SearchBackoff(int searches) =>
        TimeSpan.FromHours(searches switch
        {
            <= 1 => 6,
            2 => 12,
            _ => 24
        });

    /// <summary>An approved request searches when its backoff expired, or when it never searched.</summary>
    public static bool IsSearchDue(
        ReleaseRequestPayload payload,
        DateTime nowUtc) =>
        payload.NextSearchUtc is { } next
            ? next <= nowUtc
            : payload.Searches == 0;

    /// <summary>The payload after a bad release: the next search runs right away and names the problem.</summary>
    public static TPayload AfterProblem<TPayload>(
        TPayload payload,
        string? problem)
        where TPayload : ReleaseRequestPayload =>
        payload with
        {
            LastProblem = string.IsNullOrWhiteSpace(problem)
                ? "The previous release could not be used."
                : problem.Trim(),
            NextSearchUtc = null
        };

    public static string WithProblem(
        ReleaseRequestPayload payload,
        string message) =>
        payload.LastProblem is null
            ? message
            : $"{payload.LastProblem} {message}";

    /// <summary>
    /// Runs one search result through the lifecycle. <paramref name="candidates"/> are the
    /// releases the media search accepted, best first; <paramref name="noReleaseReason"/>
    /// explains an empty result. <paramref name="submit"/> sends the chosen release. When <paramref name="searchUnavailable"/> says that no indexer
    /// could answer, an empty result says nothing about the media: the outage is not counted as a search, so it can never use up the searches that
    /// end in giving up, and the next search comes soon (<see cref="UnavailableRetry"/>) instead of after the back-off meant for "nothing exists".
    /// </summary>
    public async Task<AcquisitionExecution> ContinueAsync<TPayload>(
        AcquisitionRequest request,
        TPayload payload,
        IReadOnlyList<ReleaseRequestCandidate> candidates,
        string noReleaseReason,
        Func<ReleaseRequestCandidate, Task<ReleaseRequestSubmission>> submit,
        CancellationToken cancellationToken,
        bool searchUnavailable = false)
        where TPayload : ReleaseRequestPayload
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(submit);

        var searches = payload.Searches + 1;
        var tried = new HashSet<string>(
            payload.TriedReleases ?? [],
            StringComparer.OrdinalIgnoreCase);
        var next = candidates.FirstOrDefault(candidate => !tried.Contains(candidate.Identity));

        if (next is null && candidates.Count == 0 && searchUnavailable)
        {
            var retry = Now + UnavailableRetry;
            await SaveAsync(request, payload with { NextSearchUtc = retry, LastProblem = null }, cancellationToken);
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Approved,
                WithProblem(payload, $"{noReleaseReason} Searching again {retry:yyyy-MM-dd HH:mm} UTC."));
        }

        if (next is null)
        {
            var reason = candidates.Count > 0
                ? EveryReleaseTried
                : noReleaseReason;

            if (searches >= MaxSearches)
            {
                await SaveAsync(
                    request,
                    payload with { Searches = searches, NextSearchUtc = null, LastProblem = null },
                    cancellationToken);
                return new AcquisitionExecution(
                    AcquisitionRequestStatus.Failed,
                    WithProblem(payload, $"{reason} Gave up after {searches} searches."));
            }

            var nextSearch = Now + SearchBackoff(searches);
            await SaveAsync(
                request,
                payload with { Searches = searches, NextSearchUtc = nextSearch, LastProblem = null },
                cancellationToken);
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Approved,
                WithProblem(payload, $"{reason} Searching again {nextSearch:yyyy-MM-dd HH:mm} UTC."));
        }

        tried.Add(next.Identity);
        var triedReleases = tried.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var saved = (TPayload)await SaveAsync(
            request,
            payload with
            {
                TriedReleases = triedReleases,
                Searches = searches,
                NextSearchUtc = null,
                LastProblem = null
            },
            cancellationToken);

        var outcome = await submit(next);
        if (outcome.Accepted && outcome.Completed is not null)
        {
            return outcome.Completed;
        }

        if (outcome.Accepted)
        {
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Downloading,
                payload.LastProblem is null
                    ? next.Title
                    : $"{payload.LastProblem} Trying {next.Title}.",
                outcome.OperationId);
        }

        // The download client refused this release; it stays tried and the request searches
        // again later instead of failing on a temporary client problem.
        var problem = string.IsNullOrWhiteSpace(outcome.Message)
            ? "The download client did not accept the release."
            : outcome.Message.Trim();
        if (searches >= MaxSearches)
        {
            await SaveAsync(
                request,
                saved with
                {
                    TriedReleases = triedReleases,
                    Searches = searches,
                    NextSearchUtc = null,
                    LastProblem = problem
                },
                cancellationToken);
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Failed,
                problem,
                outcome.OperationId);
        }

        var retryAt = Now + SearchBackoff(searches);
        await SaveAsync(
            request,
            saved with
            {
                TriedReleases = triedReleases,
                Searches = searches,
                NextSearchUtc = retryAt,
                LastProblem = problem
            },
            cancellationToken);
        return new AcquisitionExecution(
            AcquisitionRequestStatus.Approved,
            $"{problem} Searching again {retryAt:yyyy-MM-dd HH:mm} UTC.",
            outcome.OperationId);
    }

    /// <summary>
    /// The upgrade pass of an installed target: when every release that would improve it was tried or none exists, the request waits a bounded
    /// <see cref="Selection.UpgradePolicy.SearchInterval"/> instead of counting a failed search (nothing is missing), and the wait is stored so it
    /// survives a restart. Returns null while an untried improvement exists, so the caller grabs it through <see cref="ContinueAsync{TPayload}"/>.
    /// </summary>
    public async Task<AcquisitionExecution?> WaitForUpgradeAsync<TPayload>(
        AcquisitionRequest request,
        TPayload payload,
        IReadOnlyList<ReleaseRequestCandidate> improvements,
        string message,
        CancellationToken cancellationToken)
        where TPayload : ReleaseRequestPayload
    {
        var tried = new HashSet<string>(payload.TriedReleases ?? [], StringComparer.OrdinalIgnoreCase);
        if (improvements.Any(candidate => !tried.Contains(candidate.Identity)))
        {
            return null;
        }

        var next = Now + Selection.UpgradePolicy.SearchInterval;
        await SaveAsync(request, payload with { Searches = 0, LastProblem = null, NextSearchUtc = next }, cancellationToken);
        return new AcquisitionExecution(AcquisitionRequestStatus.Approved, $"{message} Looking again {next:yyyy-MM-dd HH:mm} UTC.");
    }

    /// <summary>
    /// Stores the search state of a payload; fields another owner changed meanwhile survive (see <see cref="ReleaseRequestPayload.Reconcile"/>).
    /// Returns the payload as stored, which a later save of the same run continues from so it is not treated as older than the edit this one met.
    /// </summary>
    public async Task<ReleaseRequestPayload> SaveAsync(AcquisitionRequest request, ReleaseRequestPayload payload, CancellationToken cancellationToken)
    {
        var merged = payload;
        await requests.PatchPayloadAsync(request.Id, stored => (merged = payload.Reconcile(stored)).Serialize(), cancellationToken);
        return merged;
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
}

/// <summary>
/// Wanted policy for release-backed requests (Books, Manga, Light Novels): due searches follow
/// the shared backoff and a bad release records the problem and continues with the next one.
/// </summary>
public abstract class ReleaseRequestWantedHandler(
    AcquisitionAccessStore store,
    AcquisitionRequestService requests) : IWantedRequestHandler
{
    public abstract MediaAcquisitionKind Kind { get; }

    protected abstract ReleaseRequestPayload ReadPayload(AcquisitionRequest request);

    public bool IsSearchDue(
        AcquisitionRequest request,
        DateTime nowUtc) =>
        ReleaseRequestTracker.IsSearchDue(ReadPayload(request), nowUtc);

    public async Task ContinueAfterProblemAsync(
        AcquisitionRequest request,
        string problem,
        CancellationToken cancellationToken)
    {
        var payload = ReleaseRequestTracker.AfterProblem(ReadPayload(request), problem);
        await store.UpdatePayloadAsync(
            request.Id,
            payload.Serialize(),
            cancellationToken);
        await requests.ContinueAsync(
            request.Id,
            cancellationToken);
    }
}
