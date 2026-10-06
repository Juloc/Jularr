using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Metadata;

/// <summary>What one spool run did; <see cref="ProviderPause"/> asks the worker to stop until the provider can take requests again.</summary>
public sealed record WorkMetadataRunOutcome(WorkMetadataRefreshStatus Status, TimeSpan? ProviderPause);

/// <summary>
/// Runs one claimed <c>(Work, locale)</c> of the metadata spool (#820): fetches the provider snapshot, persists text before artwork,
/// and records the outcome on the spool entry. The ladder of <see cref="MetadataFieldSources"/> decides every overwrite, so a manual
/// correction survives any refresh, and an empty provider value never erases a stored one. A provider failure never touches stored
/// metadata: it is classified into a pause (rate limit, open circuit), a retry with backoff (transient) or a long re-probe (the
/// provider says the title does not exist), and its root cause is kept on the entry.
/// </summary>
public sealed partial class WorkMetadataRefresher(
    AppDbContext db,
    WorkMetadataStore store,
    WorkService works,
    TmdbDiscoveryProvider tmdb,
    WorkArtworkCache artwork,
    TimeProvider clock,
    ILogger<WorkMetadataRefresher> logger)
{
    /// <summary>How long fetched metadata counts as fresh before the spool refreshes it in the background.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    /// <summary>When a title the provider does not know is asked about again (the negative result expires).</summary>
    public static readonly TimeSpan PermanentFailureRecheck = TimeSpan.FromDays(7);

    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);

    /// <summary>How long the spool waits after the provider's circuit opened.</summary>
    public static readonly TimeSpan UnavailablePause = TimeSpan.FromMinutes(5);

    /// <summary>How long the spool waits after the provider refused the configured credentials.</summary>
    public static readonly TimeSpan CredentialsPause = TimeSpan.FromMinutes(30);

    private const int MaxErrorLength = 2000;

    public bool IsProviderConfigured => tmdb.IsConfigured;

    /// <summary>The first transient retry waits a minute; each further failure doubles it, up to <see cref="MaxBackoff"/>.</summary>
    public static TimeSpan Backoff(int attempts)
    {
        var minutes = Math.Pow(2, Math.Clamp(attempts - 1, 0, 16));
        return TimeSpan.FromMinutes(Math.Min(minutes, MaxBackoff.TotalMinutes));
    }

    public async Task<WorkMetadataRunOutcome> RunAsync(WorkMetadataRefreshClaim claim, CancellationToken cancellationToken)
    {
        try
        {
            return await RefreshAsync(claim, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A failure outside the provider call (storage, a bug) must not turn into a hot loop on one entry: back off like a
            // transient failure and keep the cause for diagnostics.
            var attempts = claim.Attempts + 1;
            await RecordAsync(claim, WorkMetadataRefreshStatus.Queued, claim.Priority, attempts, Backoff(attempts), exception, cancellationToken);
            return new WorkMetadataRunOutcome(WorkMetadataRefreshStatus.Queued, null);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task<WorkMetadataRunOutcome> RefreshAsync(WorkMetadataRefreshClaim claim, CancellationToken cancellationToken)
    {
        var tmdbId = await store.FindTmdbIdAsync(claim.WorkId, claim.MediaType, cancellationToken);
        if (tmdbId is null)
        {
            await RecordAsync(claim, WorkMetadataRefreshStatus.Failed, WorkMetadataRefreshPriority.Library, claim.Attempts, PermanentFailureRecheck, "The Work has no TMDB identity.", cancellationToken);
            return new WorkMetadataRunOutcome(WorkMetadataRefreshStatus.Failed, null);
        }

        WorkMetadataSnapshot snapshot;
        try
        {
            var mediaType = claim.MediaType == WorkMediaType.Movie ? TmdbDiscoveryMediaType.Movie : TmdbDiscoveryMediaType.Series;
            snapshot = await tmdb.GetWorkMetadataAsync(mediaType, tmdbId, claim.Locale, cancellationToken);
        }
        catch (ProviderRateLimitedException exception)
        {
            await RecordAsync(claim, claim.Status, claim.Priority, claim.Attempts, exception.RetryAfter, exception, cancellationToken);
            return new WorkMetadataRunOutcome(claim.Status, exception.RetryAfter);
        }
        catch (ProviderUnavailableException exception)
        {
            await RecordAsync(claim, claim.Status, claim.Priority, claim.Attempts, UnavailablePause, exception, cancellationToken);
            return new WorkMetadataRunOutcome(claim.Status, UnavailablePause);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.TooManyRequests)
        {
            await RecordAsync(claim, claim.Status, claim.Priority, claim.Attempts, UnavailablePause, exception, cancellationToken);
            return new WorkMetadataRunOutcome(claim.Status, UnavailablePause);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // The credentials, not the title, are the problem: every other entry would fail the same way.
            await RecordAsync(claim, claim.Status, claim.Priority, claim.Attempts, CredentialsPause, exception, cancellationToken);
            return new WorkMetadataRunOutcome(claim.Status, CredentialsPause);
        }
        catch (Exception exception) when (exception is HttpRequestException { StatusCode: HttpStatusCode.NotFound } or JsonException or InvalidDataException)
        {
            // The provider answered: the title is gone or its answer is unusable. A valid negative result, not an outage.
            await RecordAsync(claim, WorkMetadataRefreshStatus.Failed, WorkMetadataRefreshPriority.Library, claim.Attempts + 1, PermanentFailureRecheck, exception, cancellationToken);
            return new WorkMetadataRunOutcome(WorkMetadataRefreshStatus.Failed, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or IOException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            var attempts = claim.Attempts + 1;
            await RecordAsync(claim, WorkMetadataRefreshStatus.Queued, claim.Priority, attempts, Backoff(attempts), exception, cancellationToken);
            return new WorkMetadataRunOutcome(WorkMetadataRefreshStatus.Queued, null);
        }

        await PersistTextAsync(claim.WorkId, snapshot, cancellationToken);
        var artworkFailure = await PersistArtworkAsync(claim.WorkId, snapshot, cancellationToken);
        if (artworkFailure is not null)
        {
            // The text is stored; only the images are retried, with the same backoff as any transient failure.
            var attempts = claim.Attempts + 1;
            await RecordAsync(claim, WorkMetadataRefreshStatus.Queued, claim.Priority, attempts, Backoff(attempts), artworkFailure, cancellationToken);
            return new WorkMetadataRunOutcome(WorkMetadataRefreshStatus.Queued, null);
        }

        await RecordAsync(claim, WorkMetadataRefreshStatus.Fresh, WorkMetadataRefreshPriority.Stale, 0, StaleAfter, lastError: (string?)null, cancellationToken);
        return new WorkMetadataRunOutcome(WorkMetadataRefreshStatus.Fresh, null);
    }

    /// <summary>Facts, localized text and credits in one transaction, so a reader never sees half of a refresh.</summary>
    private async Task PersistTextAsync(Guid workId, WorkMetadataSnapshot snapshot, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var source = snapshot.Source;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var facts = await store.LoadFactsAsync(workId, cancellationToken) ?? new WorkMetadataFacts { WorkId = workId };
        async Task<bool> MayReplace(string field, bool hasValue) =>
            hasValue && await works.SetFieldProvenanceAsync(workId, field, source, snapshot.ProviderExternalId, 1.0, isManualOverride: false, preferredProvider: source, cancellationToken);

        if (await MayReplace(WorkMetadataFactFields.OriginalTitle, snapshot.OriginalTitle is not null))
        {
            facts.OriginalTitle = snapshot.OriginalTitle;
        }

        if (await MayReplace(WorkMetadataFactFields.OriginalLanguage, snapshot.OriginalLanguage is not null))
        {
            facts.OriginalLanguage = snapshot.OriginalLanguage;
        }

        if (await MayReplace(WorkMetadataFactFields.ReleaseDate, snapshot.ReleaseDate is not null))
        {
            facts.ReleaseDate = snapshot.ReleaseDate;
        }

        if (await MayReplace(WorkMetadataFactFields.Runtime, snapshot.RuntimeMinutes is not null))
        {
            facts.RuntimeMinutes = snapshot.RuntimeMinutes;
        }

        if (await MayReplace(WorkMetadataFactFields.Rating, snapshot.Rating is not null))
        {
            facts.Rating = snapshot.Rating;
            facts.RatingCount = snapshot.RatingCount;
        }

        if (await MayReplace(WorkMetadataFactFields.Certification, snapshot.Certification is not null))
        {
            facts.Certification = snapshot.Certification;
            facts.CertificationCountry = snapshot.CertificationCountry;
        }

        if (await MayReplace(WorkMetadataFactFields.Studios, snapshot.Studios.Count > 0))
        {
            facts.Studios = [.. snapshot.Studios];
        }

        if (await MayReplace(WorkMetadataFactFields.ProductionCountries, snapshot.ProductionCountries.Count > 0))
        {
            facts.ProductionCountries = [.. snapshot.ProductionCountries];
        }

        facts.UpdatedAt = now;
        await store.UpsertFactsAsync(facts, cancellationToken);

        var stored = await store.LoadLocalizedStatesAsync(workId, snapshot.Locale, cancellationToken);
        var priority = MetadataFieldSources.PriorityFor(source, isManualOverride: false, preferredProvider: source);
        IReadOnlyList<(WorkLocalizedField Field, IReadOnlyList<string> Values)> fields =
        [
            (WorkLocalizedField.Title, snapshot.Title is null ? [] : [snapshot.Title]),
            (WorkLocalizedField.Overview, snapshot.Overview is null ? [] : [snapshot.Overview]),
            (WorkLocalizedField.Tagline, snapshot.Tagline is null ? [] : [snapshot.Tagline]),
            (WorkLocalizedField.Genre, snapshot.Genres),
            (WorkLocalizedField.Trailer, snapshot.TrailerKeys)
        ];
        foreach (var (field, values) in fields.Where(x => x.Values.Count > 0))
        {
            // A list field is one group: one protected or higher-priority row keeps the whole list.
            var current = stored.Where(x => x.Field == field).ToArray();
            if (current.All(x => MetadataFieldSources.ShouldReplace(x.Source, x.IsManualOverride, source, incomingIsManualOverride: false, preferredProvider: source)))
            {
                await store.ReplaceLocalizedFieldAsync(workId, snapshot.Locale, field, [.. values.Select(Bounded)], source, snapshot.ProviderExternalId, priority, now, cancellationToken);
            }
        }

        if (snapshot.Credits.Count > 0)
        {
            await store.ReplaceCreditsAsync(workId, snapshot.Credits, source, now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Keeps the variants <see cref="WorkArtworkSelection.SelectForLocale"/> chooses, poster first. An unchanged variant whose
    /// derivative is on disk is not downloaded again; a replaced variant's old derivative is deleted once the new row is stored.
    /// Returns the first transient download failure, after trying every variant.
    /// </summary>
    private async Task<Exception?> PersistArtworkAsync(Guid workId, WorkMetadataSnapshot snapshot, CancellationToken cancellationToken)
    {
        var stored = await store.LoadArtworkAsync(workId, cancellationToken);
        Exception? failure = null;
        foreach (var candidate in WorkArtworkSelection.SelectForLocale(snapshot.Artwork, snapshot.Locale))
        {
            var current = stored.FirstOrDefault(x => x.Slot == candidate.Slot && x.Language == candidate.Language);
            if (current is { IsManualOverride: true })
            {
                continue;
            }

            var cacheKey = WorkArtworkCache.CacheKey(workId, candidate.Slot, candidate.Language, snapshot.Source, candidate.ProviderFilePath);
            if (current?.CacheKey == cacheKey && File.Exists(artwork.PathFor(cacheKey)))
            {
                continue;
            }

            try
            {
                if (!await artwork.EnsureAsync(candidate.DownloadUri, candidate.Slot, cacheKey, cancellationToken))
                {
                    continue;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                failure ??= exception;
                continue;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            if (await store.UpsertArtworkAsync(workId, candidate, snapshot.Source, cacheKey, now, cancellationToken) && current?.CacheKey is { } previous && previous != cacheKey)
            {
                artwork.Delete(previous);
            }
        }

        return failure;
    }

    private Task RecordAsync(WorkMetadataRefreshClaim claim, WorkMetadataRefreshStatus status, WorkMetadataRefreshPriority priority, int attempts, TimeSpan due, Exception exception, CancellationToken cancellationToken) =>
        RecordAsync(claim, status, priority, attempts, due, Describe(exception), cancellationToken);

    private Task RecordAsync(WorkMetadataRefreshClaim claim, WorkMetadataRefreshStatus status, WorkMetadataRefreshPriority priority, int attempts, TimeSpan due, string? lastError, CancellationToken cancellationToken)
    {
        if (lastError is not null)
        {
            // The run is the job boundary: its failure is logged once, here, with the cause the entry keeps.
            logger.LogWarning("Work metadata of {WorkId} ({Locale}) was not refreshed ({Status}): {Error}", claim.WorkId, claim.Locale, status, lastError);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        // A shutdown must not lose the outcome of a finished run.
        var token = cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken;
        return store.RecordRunAsync(claim.Id, status, priority, attempts, now + due, lastError, now, token);
    }

    /// <summary>The exception chain as diagnostics: types and messages down to the root cause, credentials removed, bounded.</summary>
    public static string Describe(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null && parts.Count < 8; current = current.InnerException)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
        }

        var text = Secrets().Replace(string.Join(" -> ", parts), "$1=[redacted]");
        return text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];
    }

    private static string Bounded(string value) => value.Length <= 4000 ? value : value[..4000];

    [GeneratedRegex("(api_key|access_token|Bearer)[=: ]+[^&\\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex Secrets();
}
