using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Wanted;

namespace Jularr.Web.Features.Audiobooks;

/// <summary>What an audiobook request searches for: the book's title and author. The search state is the shared <see cref="ReleaseRequestPayload"/>.</summary>
public sealed record AudiobookRequestPayload(string Title, string? Author) : ReleaseRequestPayload;

/// <summary>How many words of the requested title and author a candidate's name repeats.</summary>
public sealed record AudiobookMatch(int MatchedTitleWords, int AuthorHits);

// Judges Usenet releases for one audiobook: the title words must match, an e-book release is never an audiobook, and the audio container is the quality.
public static class AudiobookReleaseJudge
{
    private static readonly string[] EbookFormats = ["epub", "pdf", "mobi", "azw3", "azw", "djvu", "cbz", "cbr"];
    private static readonly string[] AudioWords = ["m4b", "mp3", "m4a", "aac", "flac", "audiobook", "audio", "unabridged", "hörbuch"];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase) { "the", "a", "an", "of", "and", "der", "die", "das", "und", "des", "le", "la", "les" };

    public static MediaSearchPlan<AudiobookMatch> Plan(string title, string? author)
    {
        var titleWords = Words(SearchPlanner.MainTitle(title));
        var authorWords = Words(author);
        var intent = new SearchIntent(MediaAcquisitionKind.Audiobook, title.Trim()) { Creator = string.IsNullOrWhiteSpace(author) ? null : author.Trim() };
        return new MediaSearchPlan<AudiobookMatch>(intent, release => Judge(release, titleWords, authorWords));
    }

    private static ReleaseJudgement<AudiobookMatch> Judge(AcquisitionCandidate release, IReadOnlyCollection<string> titleWords, IReadOnlyCollection<string> authorWords)
    {
        var words = Words(release.Title);
        var matchedTitle = titleWords.Count(words.Contains);
        var authorHits = authorWords.Count(words.Contains);
        var parsed = AudiobookReleaseParser.Instance.Parse(release.Title);
        var safety = release.InternalDownloadUri is null
            ? "no download link"
            : release.Protocol is not null && !release.Protocol.Equals("usenet", StringComparison.OrdinalIgnoreCase)
                ? "not a Usenet release"
                : !words.Overlaps(AudioWords) && words.Overlaps(EbookFormats) ? "an e-book release, not an audiobook" : null;
        var identity = titleWords.Count == 0
            ? ReleaseIdentityEvidence.Conflict("EmptyTitle", "empty title")
            : matchedTitle < titleWords.Count
                ? ReleaseIdentityEvidence.Conflict("TitleDoesNotMatch", "title does not match")
                : authorHits > 0
                    ? ReleaseIdentityEvidence.Exact("TitleAndAuthor", "Title and author match.")
                    : ReleaseIdentityEvidence.Strong("Title", "The title matches.");
        return new ReleaseJudgement<AudiobookMatch>(new AudiobookMatch(matchedTitle, authorHits), parsed, identity, SelectionCoverage.Single, safety);
    }

    private static HashSet<string> Words(string? value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in (value ?? string.Empty).Split([' ', '.', '_', '-', ':', ',', '(', ')', '[', ']', '\'', '"', '!', '?', '&', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length > 1 && !StopWords.Contains(word))
            {
                result.Add(word.ToLowerInvariant());
            }
        }

        return result;
    }
}

/// <summary>Automatic audiobook acquisition on the shared core: Usenet releases are ranked by the one selection and the winner goes to the download client and the audiobook importer.</summary>
public sealed class AudiobookAcquisitionRequestExecutor(
    IndexerSearchCoordinator indexers,
    DownloadClientStore downloadClients,
    AcquisitionCore core,
    QualityProfileStore profiles,
    RequestWorkBinder? binder = null) : IAcquisitionRequestExecutor
{
    public const string OperationKind = "audiobook-usenet-download";

    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Audiobook;

    public async Task<AcquisitionExecution> ExecuteAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        request = binder is null ? request : await binder.EnsureBoundAsync(request, cancellationToken);
        var payload = ReadPayload(request);
        if (!await indexers.HasEnabledIndexerAsync(cancellationToken))
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, "No Usenet indexer is configured.");
        }

        if (!(await downloadClients.LoadAllAsync(cancellationToken)).Any(entry => entry.Enabled))
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, "No download client is configured.");
        }

        var profile = await profiles.ResolveAsync(MediaAcquisitionKind.Audiobook, request.WorkId, cancellationToken);
        var search = await core.SearchAsync(AudiobookReleaseJudge.Plan(payload.Title, payload.Author), profile, new SearchOptions(), cancellationToken);
        var failure = search.Releases.Count == 0
            ? search.Search.Warnings.Count > 0 ? $"No release found on the indexers ({search.Search.Warnings[0].IndexerName}: {search.Search.Warnings[0].Message})." : "No release found on the indexers."
            : "No suitable audiobook release found on the indexers.";
        return await core.GrabAsync(
            request,
            payload,
            search.Grabbable,
            failure,
            new GrabTarget(OperationKind, "Download Audiobook", payload.Title, MediaAcquisitionKind.Audiobook, string.Empty),
            cancellationToken,
            searchUnavailable: search.Search.EveryIndexerFailed);
    }

    public static AudiobookRequestPayload ReadPayload(AcquisitionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.PayloadJson))
        {
            try
            {
                if (JsonSerializer.Deserialize<AudiobookRequestPayload>(request.PayloadJson, JsonSerializerOptions.Web) is { } persisted && !string.IsNullOrWhiteSpace(persisted.Title))
                {
                    return persisted;
                }
            }
            catch (JsonException)
            {
            }
        }

        // For an audiobook request the subtitle carries the author.
        return new AudiobookRequestPayload(request.Title, request.Subtitle);
    }
}

/// <summary>Audiobook requests on the shared release-request Wanted policy.</summary>
public sealed class AudiobookWantedRequestHandler(AcquisitionAccessStore store, AcquisitionRequestService requests) : ReleaseRequestWantedHandler(store, requests)
{
    public override MediaAcquisitionKind Kind => MediaAcquisitionKind.Audiobook;

    protected override ReleaseRequestPayload ReadPayload(AcquisitionRequest request) => AudiobookAcquisitionRequestExecutor.ReadPayload(request);
}
