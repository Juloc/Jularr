using Jularr.Web.Features.Acquisition.Search;
using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.ManualSearch;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.ReadingDiscovery;
using Jularr.Web.Features.ReadingSources;

namespace Jularr.Web.Features.ReadingAcquisition;

/// <summary>
/// What a Manga or Light Novel request searches for. The Usenet search state (tried releases,
/// searches, next search, last problem) is the shared <see cref="ReleaseRequestPayload"/>.
/// </summary>
public sealed record ReadingRequestPayload(
    string Title,
    IReadOnlyList<string> Aliases,
    string? Author,
    int? RequestedVolume = null,
    double? RequestedChapterStart = null,
    double? RequestedChapterEnd = null,
    IReadOnlyList<string>? PreferredLanguages = null) : ReleaseRequestPayload;

public sealed class ReadingAcquisitionEngine(
    IndexerSearchCoordinator indexers,
    DownloadClientStore downloadClients,
    AcquisitionCore core,
    QualityProfileStore? profiles = null,
    RequestWorkBinder? binder = null)
{
    public const string OperationKind = "reading-usenet-download";

    public async Task<AcquisitionExecution> ExecuteAsync(
        AcquisitionRequest request,
        ReadingAcquisitionTarget initialTarget,
        CancellationToken cancellationToken)
    {
        if (request.Kind is not (MediaAcquisitionKind.Manga or MediaAcquisitionKind.LightNovel))
        {
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Failed,
                "Reading acquisition only supports Manga and Light Novels.");
        }

        // A request made before the Work binding, or one whose identity could not be resolved then, is bound now; the profile below is the Work's.
        request = binder is null ? request : await binder.EnsureBoundAsync(request, cancellationToken);
        var payload = ReadPayload(request, initialTarget);
        var target = ToTarget(request.Kind, payload);

        // Without Usenet configured only a direct source (a public web copy) can serve the request.
        var usenetProblem = !await indexers.HasEnabledIndexerAsync(cancellationToken)
            ? "No Usenet indexer is configured."
            : !(await downloadClients.LoadAllAsync(cancellationToken)).Any(entry => entry.Enabled && entry.Type == DownloadClientType.Sabnzbd)
                ? "SABnzbd is not configured."
                : null;
        var profile = profiles is null ? ReadingQualityProfiles.For(request.Kind) : await profiles.ResolveAsync(request.Kind, request.WorkId, cancellationToken);
        var search = await core.SearchAsync(ReadingReleaseJudge.Plan(target), profile, new SearchOptions(), cancellationToken);
        var grabbable = search.Grabbable.Where(release => usenetProblem is null || release.Candidate.Type == AcquisitionType.DirectImport).ToArray();
        return grabbable.Length == 0 && usenetProblem is not null
            ? new AcquisitionExecution(AcquisitionRequestStatus.Failed, usenetProblem)
            : await GrabAsync(request, payload, grabbable, FailureMessage(search), cancellationToken, searchUnavailable: search.Search.EveryIndexerFailed);
    }

    // Runs the shared grab over the releases (best first); Manual Search passes the one the owner selected.
    public async Task<AcquisitionExecution> GrabAsync(
        AcquisitionRequest request,
        ReadingRequestPayload payload,
        IReadOnlyList<ReleaseEvaluation<ReadingReleaseInfo>> releases,
        string noReleaseReason,
        CancellationToken cancellationToken,
        ManualGrabProgress? progress = null,
        bool searchUnavailable = false) =>
        await core.GrabAsync(
            request,
            payload,
            releases,
            noReleaseReason,
            new GrabTarget(OperationKind, request.Kind == MediaAcquisitionKind.Manga ? "Download Manga" : "Download Light Novel", payload.Title, request.Kind, string.Empty),
            cancellationToken,
            progress,
            searchUnavailable);

    public static string FailureMessage(SearchEvaluation<ReadingReleaseInfo> search) =>
        search.Releases.Count == 0
            ? search.Search.Warnings.Count > 0
                ? $"No release found on the indexers ({search.Search.Warnings[0].IndexerName}: {search.Search.Warnings[0].Message})."
                : "No release found on the indexers."
            : "No suitable release matched the requested title, format, volume, chapter or language.";

    public static ReadingRequestPayload ReadPayload(
        AcquisitionRequest request,
        ReadingAcquisitionTarget fallback)
    {
        if (!string.IsNullOrWhiteSpace(request.PayloadJson))
        {
            try
            {
                var persisted = JsonSerializer.Deserialize<ReadingRequestPayload>(
                    request.PayloadJson,
                    JsonSerializerOptions.Web);
                if (persisted is not null &&
                    !string.IsNullOrWhiteSpace(persisted.Title))
                {
                    return persisted;
                }
            }
            catch (JsonException)
            {
            }
        }

        return new ReadingRequestPayload(
            fallback.Title,
            fallback.Aliases,
            fallback.Author,
            fallback.RequestedVolume,
            fallback.RequestedChapterStart,
            fallback.RequestedChapterEnd,
            fallback.PreferredLanguages);
    }

    /// <summary>
    /// The payload a Light Novel request starts with: the native title is a search alias and
    /// the author stays the author, so neither is mistaken for the other later.
    /// </summary>
    public static string LightNovelDraftPayload(
        string title,
        string? nativeTitle,
        string? author) =>
        JsonSerializer.Serialize(
            new ReadingRequestPayload(
                title.Trim(),
                string.IsNullOrWhiteSpace(nativeTitle) ||
                nativeTitle.Trim().Equals(title.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? []
                    : [nativeTitle.Trim()],
                string.IsNullOrWhiteSpace(author) ? null : author.Trim()),
            JsonSerializerOptions.Web);

    /// <summary>What a request without a stored payload searches for: a Manga request carries its native title as the alias, a Light Novel request its author.</summary>
    public static ReadingAcquisitionTarget FallbackTarget(AcquisitionRequest request) =>
        request.Kind == MediaAcquisitionKind.LightNovel
            ? new ReadingAcquisitionTarget(MediaAcquisitionKind.LightNovel, request.Title, [], request.Subtitle)
            : new ReadingAcquisitionTarget(MediaAcquisitionKind.Manga, request.Title, string.IsNullOrWhiteSpace(request.Subtitle) ? [] : [request.Subtitle.Trim()]);

    public static ReadingAcquisitionTarget ToTarget(
        MediaAcquisitionKind kind,
        ReadingRequestPayload payload) =>
        new(
            kind,
            payload.Title,
            payload.Aliases ?? [],
            payload.Author,
            payload.RequestedVolume,
            payload.RequestedChapterStart,
            payload.RequestedChapterEnd,
            payload.PreferredLanguages);
}

public sealed class MangaAcquisitionRequestExecutor(
    ReadingAcquisitionEngine engine) : IAcquisitionRequestExecutor
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Manga;

    public Task<AcquisitionExecution> ExecuteAsync(
        AcquisitionRequest request,
        CancellationToken cancellationToken)
    {
        return engine.ExecuteAsync(request, ReadingAcquisitionEngine.FallbackTarget(request), cancellationToken);
    }
}

public sealed class LightNovelAcquisitionRequestExecutor(
    ReadingAcquisitionEngine engine,
    NovelAniListProvider aniList,
    NovelImportService webNovels) : IAcquisitionRequestExecutor
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.LightNovel;

    public async Task<AcquisitionExecution> ExecuteAsync(
        AcquisitionRequest request,
        CancellationToken cancellationToken)
    {
        // A public Syosetu (ncode) work is a legal web source: import it directly, never
        // search Usenet for it (#485 item 3).
        if (request.Provider.Equals(
                NcodeNovelSourceProvider.ProviderKey,
                StringComparison.OrdinalIgnoreCase))
        {
            return await ImportWebNovelAsync(request, cancellationToken);
        }

        var payload = ReadingAcquisitionEngine.ReadPayload(request, ReadingAcquisitionEngine.FallbackTarget(request));

        if (payload.Searches == 0)
        {
            payload = await EnrichAsync(request, payload, cancellationToken);
            request = request with
            {
                PayloadJson = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web)
            };
        }

        return await engine.ExecuteAsync(
            request,
            ReadingAcquisitionEngine.ToTarget(MediaAcquisitionKind.LightNovel, payload),
            cancellationToken);
    }

    /// <summary>
    /// First search only: AniList gives the canonical title and the native title as an alias.
    /// Older requests stored the native title as the author; it is dropped as author here.
    /// </summary>
    private async Task<ReadingRequestPayload> EnrichAsync(
        AcquisitionRequest request,
        ReadingRequestPayload payload,
        CancellationToken cancellationToken)
    {
        var aliases = new List<string>(payload.Aliases ?? []);
        var canonicalTitle = payload.Title;
        var author = payload.Author;

        if (request.Provider.Equals(
                NovelAniListProvider.ProviderKey,
                StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var candidate = await aniList.GetAsync(
                    request.ExternalId,
                    cancellationToken);
                if (candidate is not null)
                {
                    canonicalTitle = candidate.PreferredTitle;
                    if (!string.IsNullOrWhiteSpace(candidate.NativeTitle))
                    {
                        aliases.Add(candidate.NativeTitle);
                        if (string.Equals(author?.Trim(), candidate.NativeTitle.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            author = null;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is NovelMetadataProviderException or
                HttpRequestException or
                TaskCanceledException)
            {
                // Catalog metadata is enrichment only. The request title still gives the
                // indexer search a stable canonical query.
            }
        }

        if (!canonicalTitle.Equals(request.Title, StringComparison.OrdinalIgnoreCase))
        {
            aliases.Add(request.Title);
        }

        return payload with
        {
            Title = canonicalTitle,
            Aliases = aliases
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias.Trim())
                .Where(alias => !alias.Equals(canonicalTitle, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Author = string.IsNullOrWhiteSpace(author) ? null : author.Trim()
        };
    }

    private async Task<AcquisitionExecution> ImportWebNovelAsync(
        AcquisitionRequest request,
        CancellationToken cancellationToken)
    {
        if (!SyosetuCatalogClient.IsValidNcode(request.ExternalId))
        {
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Failed,
                "This Syosetu request has no valid ncode.");
        }

        var sourceUrl = $"https://ncode.syosetu.com/{request.ExternalId.Trim().ToLowerInvariant()}/";
        try
        {
            var workId = await webNovels.ImportWorkAsync(sourceUrl, cancellationToken);
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Completed,
                "Imported from Syosetu.",
                ResultUrl: $"/Novels/Work/{workId}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            HttpRequestException or
            TaskCanceledException)
        {
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Failed,
                $"The Syosetu import failed: {exception.Message.Trim().TrimEnd('.')}. Approve the request again to retry.");
        }
    }
}
