using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Music;

/// <summary>
/// What a Music request searches for: one album of one artist. The Usenet search state (tried releases, searches, next search, last
/// problem) is the shared <see cref="ReleaseRequestPayload"/>; the Work is the album's canonical identity.
/// </summary>
public sealed record MusicRequestPayload(Guid WorkId, string Artist, string Album, int? Year) : ReleaseRequestPayload
{
    /// <summary>The payload of a request; one that carries none (or a broken one) is rebuilt from the request fields so an old row never breaks a page.</summary>
    public static MusicRequestPayload Of(AcquisitionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.PayloadJson))
        {
            try
            {
                if (JsonSerializer.Deserialize<MusicRequestPayload>(request.PayloadJson, JsonSerializerOptions.Web) is { } stored && !string.IsNullOrWhiteSpace(stored.Album))
                {
                    return stored;
                }
            }
            catch (JsonException)
            {
            }
        }

        return new MusicRequestPayload(Guid.Empty, request.Subtitle ?? string.Empty, request.Title, null);
    }
}

public static class MusicLinks
{
    /// <summary>The Admin address of an album, built from the Work id and never from a title or a path.</summary>
    public static string AlbumPath(Guid workId) => $"/Admin/Music/Album/{workId:D}";

    public static string ArtistPath(Guid artistId) => $"/Admin/Music/Artist/{artistId:D}";
}

/// <summary>What the Music media type concluded about one release before any profile rule is looked at.</summary>
public sealed record MusicJudgement(ReleaseInfo? Parsed, ReleaseIdentityEvidence Evidence, string? SafetyRejection);

/// <summary>
/// The Music side of release identity: is this release the requested album of the requested artist. A release of another album or artist,
/// or of a different kind of release (live, remix, karaoke, a tribute), is a conflict; a collection (discography, anthology) may contain
/// the album and waits for a person. It only produces identity evidence; the shared selection engine decides what that means for the profile.
/// </summary>
public static partial class MusicReleaseJudge
{
    private static readonly HashSet<string> TitleNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "and", "feat", "featuring", "ft", "vs", "with", "remastered", "remaster", "deluxe", "edition", "expanded", "bonus", "tracks",
        "version", "anniversary", "special", "limited", "explicit", "clean", "digipak", "japan", "import", "promo", "web", "cd", "retail", "proper", "repack"
    };

    // A release of another kind than the studio album that was asked for.
    private static readonly HashSet<string> OtherKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "live", "remix", "remixes", "remixed", "karaoke", "instrumental", "instrumentals", "tribute", "cover", "covers", "demo", "demos", "acoustic", "unplugged", "bootleg", "mixtape"
    };

    private static readonly HashSet<string> Collections = new(StringComparer.OrdinalIgnoreCase)
    {
        "discography", "anthology", "collection", "complete", "boxset", "box", "greatest", "hits", "best", "albums"
    };

    [GeneratedRegex(@"^(?:19|20)\d{2}$")]
    private static partial Regex YearToken();

    public static MusicJudgement Judge(IReleaseParser parser, string artist, string album, int? year, ProwlarrReleaseCandidate candidate)
    {
        if (candidate.InternalDownloadUri is null)
        {
            return Unusable("The indexer returned no download link.");
        }

        if (candidate.Protocol is not null && !candidate.Protocol.Equals("usenet", StringComparison.OrdinalIgnoreCase))
        {
            return Unusable("Jularr only downloads Usenet releases.");
        }

        if (!parser.TryParse(candidate.Title, out var parsed))
        {
            return Unusable("The release name could not be parsed.");
        }

        var words = Words(parsed.SeriesTitle);
        var wantedAlbum = Words(album);
        var wantedArtist = Words(artist);
        var variousArtists = artist.Contains("various", StringComparison.OrdinalIgnoreCase);
        if (wantedAlbum.Count == 0 || !wantedAlbum.All(words.Contains))
        {
            return new MusicJudgement(parsed, ReleaseIdentityEvidence.Conflict("WrongAlbum", "The release is for another album."), null);
        }

        if (!variousArtists && wantedArtist.Count > 0 && !wantedArtist.All(words.Contains))
        {
            return new MusicJudgement(parsed, ReleaseIdentityEvidence.Conflict("WrongArtist", "The release is by another artist."), null);
        }

        var other = words.FirstOrDefault(word => OtherKinds.Contains(word) && !wantedAlbum.Contains(word) && !wantedArtist.Contains(word));
        if (other is not null)
        {
            return new MusicJudgement(parsed, ReleaseIdentityEvidence.Conflict("DifferentRelease", $"A '{other}' release is not the studio album."), null);
        }

        var collection = words.FirstOrDefault(word => Collections.Contains(word) && !wantedAlbum.Contains(word) && !wantedArtist.Contains(word));
        if (collection is not null)
        {
            return new MusicJudgement(parsed, ReleaseIdentityEvidence.Ambiguous("Collection", $"A '{collection}' pack may contain the album, but also much more."), null);
        }

        var years = words.Where(word => YearToken().IsMatch(word) && !wantedAlbum.Contains(word)).Select(int.Parse).ToArray();
        var yearMatches = year is not null && years.Any(candidateYear => Math.Abs(candidateYear - year.Value) <= 1);
        return new MusicJudgement(
            parsed,
            yearMatches
                ? ReleaseIdentityEvidence.Exact("Matches", "Artist, album and year match.")
                : ReleaseIdentityEvidence.Strong("Matches", years.Length > 0 && year is not null ? $"Artist and album match; the release is from {string.Join('/', years)} (a reissue)." : "Artist and album match."),
            null);
    }

    private static MusicJudgement Unusable(string reason) =>
        new(null, ReleaseIdentityEvidence.Strong("Unusable", reason), reason);

    private static HashSet<string> Words(string value) =>
        value.Split([' ', '.', '_', '-', ':', ',', '(', ')', '[', ']', '\'', '"', '!', '?', '&', '/', '+'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.ToLowerInvariant())
            .Where(word => !TitleNoise.Contains(word))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One release as the Music pipeline judged it, in selection order.</summary>
public sealed record MusicReleaseEvaluation(ProwlarrReleaseCandidate Candidate, ReleaseInfo? Parsed, CandidateEvaluation Selection)
{
    public bool IsGrabbable => Selection.IsSelectable && Candidate.InternalDownloadUri is not null;

    public bool IsManuallyGrabbable => (IsGrabbable || Selection.Decision == SelectionDecision.ManualReview) && Candidate.InternalDownloadUri is not null;
}

/// <summary>What a manual grab got through before it stopped, so a caller that sees an exception knows whether a download exists.</summary>
public sealed class MusicGrabProgress
{
    public bool SubmitStarted { get; set; }

    public bool Accepted { get; set; }

    public Guid? OperationId { get; set; }
}

public sealed record MusicSearchEvaluation(QualityProfile Profile, AcquisitionSearchResult Search, IReadOnlyList<MusicReleaseEvaluation> Releases, SelectionResult Selection)
{
    public IReadOnlyList<MusicReleaseEvaluation> Grabbable => [.. Releases.Where(release => release.IsGrabbable)];
}

/// <summary>
/// The Music request-to-download path on the shared pipeline: the album's Work and tracks, the shared Search Planner and executor, the shared
/// selection engine with the Music profile, and the shared release-request lifecycle (tried releases, back-off, give-up) that submits through
/// the shared download-client path. It owns no timer: retries, download state and the completed-import dispatch stay in the Wanted pass.
/// </summary>
public sealed class MusicAcquisitionEngine(
    AppDbContext db,
    IndexerSearchCoordinator indexers,
    DownloadClientStore downloadClients,
    DownloadClientSubmissionService downloads,
    MediaAcquisitionRegistry registry,
    QualityProfileStore profiles,
    ReleaseRequestTracker tracker,
    MusicLibraryService library,
    TimeProvider clock,
    ILogger<MusicAcquisitionEngine> logger)
{
    public const string OperationKind = "music-usenet-download";

    public async Task<AcquisitionExecution> ExecuteAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        if (request.Kind != MediaAcquisitionKind.Music)
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, "Music acquisition only supports Music requests.");
        }

        var payload = MusicRequestPayload.Of(request);
        var workId = payload.WorkId != Guid.Empty ? payload.WorkId : await ResolveWorkIdAsync(request, cancellationToken);
        if (workId is null)
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, "The canonical album for this request no longer exists.");
        }

        payload = payload with { WorkId = workId.Value };
        if (await HasAudioFilesAsync(workId.Value, cancellationToken))
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Completed, "The album is already in the library.", ResultUrl: MusicLinks.AlbumPath(workId.Value));
        }

        if (!await indexers.HasEnabledIndexerAsync(cancellationToken))
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, "No Usenet indexer is configured.");
        }

        if (!(await downloadClients.LoadAllAsync(cancellationToken)).Any(entry => entry.Enabled && entry.Type == DownloadClientType.Sabnzbd))
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, "SABnzbd is not configured.");
        }

        // The track list lets the importer match files; it is read once and a provider outage never blocks the search.
        try
        {
            await library.EnsureTracksAsync(workId.Value, cancellationToken);
        }
        catch (MusicMetadataException exception)
        {
            logger.LogInformation(exception, "The track list of album {WorkId} could not be read yet.", workId);
        }

        var profile = await profiles.ResolveAsync(MediaAcquisitionKind.Music, workId.Value, cancellationToken);
        var evaluation = await SearchAsync(request.CreatedAt, payload, profile, new SearchOptions { Purpose = SearchPurpose.Automatic }, cancellationToken);
        return await GrabAsync(request, payload, evaluation.Grabbable, FailureMessage(evaluation), cancellationToken);
    }

    /// <summary>
    /// Runs the tracker lifecycle over the given releases (best first) and submits the first untried one through the shared download-client path.
    /// Automatic acquisition passes every grabbable release; Manual Search passes the one the owner selected.
    /// </summary>
    public async Task<AcquisitionExecution> GrabAsync(
        AcquisitionRequest request,
        MusicRequestPayload payload,
        IReadOnlyList<MusicReleaseEvaluation> releases,
        string noReleaseReason,
        CancellationToken cancellationToken,
        MusicGrabProgress? progress = null)
    {
        var workId = payload.WorkId;
        var candidates = releases
            .Select(release => new ReleaseRequestCandidate(release.Candidate.Identity, release.Candidate.Title, release.Candidate.InternalDownloadUri!))
            .ToArray();
        var byIdentity = releases.ToDictionary(release => release.Candidate.Identity, release => release.Candidate, StringComparer.Ordinal);
        var title = $"{payload.Artist} - {payload.Album}";
        var execution = await tracker.ContinueAsync(
            request,
            payload,
            candidates,
            noReleaseReason,
            async release =>
            {
                progress?.SubmitStarted = true;
                var sources = byIdentity[release.Identity].Sources.Select(source => source.DownloadUri).OfType<Uri>().Distinct().ToArray();
                var outcome = await downloads.SubmitFirstAcceptedAsync(
                    sources.Length == 0 ? [release.DownloadUri] : sources,
                    uri => new DownloadSubmissionSpec(OperationKind, "Download Music", title, request.RequestedByProfileId, uri, release.Title, MediaAcquisitionKind.Music, MediaTargetKey: $"work:{workId:D}"),
                    cancellationToken);
                if (outcome.Accepted && progress is not null)
                {
                    progress.Accepted = true;
                    progress.OperationId = outcome.OperationId;
                }

                return new ReleaseRequestSubmission(outcome.Accepted, outcome.OperationId, outcome.Message);
            },
            cancellationToken);
        return execution with { ResultUrl = MusicLinks.AlbumPath(workId) };
    }

    /// <summary>The one search + selection pipeline: automatic acquisition and Manual Search read their candidates from here.</summary>
    public async Task<MusicSearchEvaluation> SearchAsync(
        DateTime wantedSinceUtc,
        MusicRequestPayload payload,
        QualityProfile profile,
        SearchOptions options,
        CancellationToken cancellationToken)
    {
        var intent = new SearchIntent(MediaAcquisitionKind.Music, payload.Album) { Creator = payload.Artist, Year = payload.Year };
        var parser = registry.ParserFor(MediaAcquisitionKind.Music);
        MusicJudgement Judge(ProwlarrReleaseCandidate release) => MusicReleaseJudge.Judge(parser, payload.Artist, payload.Album, payload.Year, release);
        var search = await indexers.SearchAsync(
            intent,
            options with { UsableCount = releases => releases.Count(release => Judge(release).Evidence.Confidence is IdentityConfidence.Exact or IdentityConfidence.Strong && Judge(release).SafetyRejection is null) },
            cancellationToken);

        var judged = search.Releases
            .GroupBy(release => release.Identity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (Release: group.First(), Judgement: Judge(group.First())), StringComparer.Ordinal);
        var wantedSince = new DateTimeOffset(DateTime.SpecifyKind(wantedSinceUtc, DateTimeKind.Utc));
        var selection = ReleaseSelectionEngine.Select(
            profile,
            new SelectionContext(clock.GetUtcNow(), wantedSince),
            [.. judged.Select(pair => new SelectionCandidate(
                pair.Key,
                pair.Value.Judgement.Parsed,
                pair.Value.Release.SizeBytes,
                pair.Value.Release.Indexer,
                pair.Value.Release.Sources.FirstOrDefault()?.Priority ?? 0,
                pair.Value.Release.PublishedAt,
                pair.Value.Judgement.Evidence,
                SelectionCoverage.Single)
            {
                SafetyRejection = pair.Value.Judgement.SafetyRejection
            })]);
        var evaluations = selection.Ranked
            .Select(ranked => new MusicReleaseEvaluation(judged[ranked.Candidate.Id].Release, judged[ranked.Candidate.Id].Judgement.Parsed, ranked))
            .ToArray();
        return new MusicSearchEvaluation(profile, search, evaluations, selection);
    }

    /// <summary>Whether any track file of the album is already in the library, so a request never downloads what exists.</summary>
    public async Task<bool> HasAudioFilesAsync(Guid workId, CancellationToken cancellationToken) =>
        await db.MediaAssets.AsNoTracking()
            .Where(asset => asset.WorkId == workId && asset.Kind == MediaAssetKind.Audio)
            .AnyAsync(asset => db.StoredFiles.Any(file => file.MediaAssetId == asset.Id), cancellationToken);

    private async Task<Guid?> ResolveWorkIdAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        var id = await db.WorkExternalIdentities.AsNoTracking()
            .Where(identity => identity.MediaType == WorkMediaType.Music && identity.Provider == request.Provider.ToLower() && identity.ExternalId == request.ExternalId.Trim().ToLower())
            .Select(identity => (Guid?)identity.WorkId)
            .FirstOrDefaultAsync(cancellationToken);
        return id;
    }

    /// <summary>What an empty or unusable search means: an indexer problem, nothing found, only other albums, or the profile refusing what was found.</summary>
    private static string FailureMessage(MusicSearchEvaluation evaluation)
    {
        var search = evaluation.Search;
        if (search.Releases.Count == 0)
        {
            return search.EveryIndexerFailed
                ? $"No indexer could be searched ({search.Warnings[0].IndexerName}: {search.Warnings[0].Message})."
                : search.Warnings.Count > 0
                    ? $"No release found ({search.Warnings[0].IndexerName}: {search.Warnings[0].Message})."
                    : "No release found on the indexers.";
        }

        return evaluation.Selection.Outcome switch
        {
            SelectionOutcome.ManualReviewOnly => "Releases were found, but their identity needs a manual decision (Manual Search).",
            SelectionOutcome.IdentityInvalid => "Releases were found, but none is the requested album.",
            _ => "No suitable release matched the requested album and quality profile."
        };
    }
}

public sealed class MusicAcquisitionRequestExecutor(MusicAcquisitionEngine engine) : IAcquisitionRequestExecutor
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Music;

    public Task<AcquisitionExecution> ExecuteAsync(AcquisitionRequest request, CancellationToken cancellationToken) =>
        engine.ExecuteAsync(request, cancellationToken);
}

/// <summary>Wanted policy of Music requests: due searches follow the shared back-off and a bad release continues with the next one.</summary>
public sealed class MusicWantedRequestHandler(AcquisitionAccessStore store, AcquisitionRequestService requests) : ReleaseRequestWantedHandler(store, requests)
{
    public override MediaAcquisitionKind Kind => MediaAcquisitionKind.Music;

    protected override ReleaseRequestPayload ReadPayload(AcquisitionRequest request) => MusicRequestPayload.Of(request);
}
