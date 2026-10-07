using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.ManualSearch;
using Jularr.Web.Features.Acquisition.Quality;
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
    DownloadClientSubmissionService downloads,
    ReleaseRequestTracker tracker,
    QualityProfileStore? profiles = null)
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

        var payload = ReadPayload(request, initialTarget);
        var target = ToTarget(request.Kind, payload);

        if (!await indexers.HasEnabledIndexerAsync(cancellationToken))
        {
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Failed,
                "No Usenet indexer is configured.");
        }

        if (!(await downloadClients.LoadAllAsync(cancellationToken))
            .Any(entry => entry.Enabled && entry.Type == DownloadClientType.Sabnzbd))
        {
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Failed,
                "SABnzbd is not configured.");
        }

        var profile = profiles is null ? null : await profiles.ResolveAsync(request.Kind, workId: null, cancellationToken);
        var search = await ReadingUsenetSearch.SearchAsync(indexers, target, cancellationToken, profile: profile);

        return await GrabAsync(request, payload, Candidates(search), search.FailureMessage, cancellationToken);
    }

    /// <summary>
    /// Runs the tracker lifecycle over the given releases (best first) and submits the first untried one through the shared download-client path.
    /// Automatic acquisition passes every accepted release; Manual Search passes the one the owner selected.
    /// </summary>
    public async Task<AcquisitionExecution> GrabAsync(
        AcquisitionRequest request,
        ReadingRequestPayload payload,
        IReadOnlyList<ReleaseRequestCandidate> candidates,
        string noReleaseReason,
        CancellationToken cancellationToken,
        ManualGrabProgress? progress = null) =>
        await tracker.ContinueAsync(
            request,
            payload,
            candidates,
            noReleaseReason,
            async release =>
            {
                progress?.SubmitStarted = true;
                var outcome = await downloads.SubmitAsync(
                    new DownloadSubmissionSpec(
                        OperationKind,
                        request.Kind == MediaAcquisitionKind.Manga
                            ? "Download Manga"
                            : "Download Light Novel",
                        payload.Title,
                        request.RequestedByProfileId,
                        release.DownloadUri,
                        release.Title,
                        request.Kind),
                    cancellationToken);
                if (outcome.Accepted && progress is not null)
                {
                    progress.Accepted = true;
                    progress.OperationId = outcome.OperationId;
                }

                return new ReleaseRequestSubmission(
                    outcome.Accepted,
                    outcome.OperationId,
                    outcome.Message);
            },
            cancellationToken);

    /// <summary>The releases the reading matcher accepted, best first; each is tried once by its identity.</summary>
    public static IReadOnlyList<ReleaseRequestCandidate> Candidates(
        ReadingUsenetSearchResult search)
    {
        ArgumentNullException.ThrowIfNull(search);

        return search.Ranked
            .Where(candidate =>
                candidate.Score > 0 &&
                candidate.Release.InternalDownloadUri is not null)
            .Select(candidate => new ReleaseRequestCandidate(
                candidate.Release.Identity,
                candidate.Release.Title,
                candidate.Release.InternalDownloadUri!))
            .ToArray();
    }

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
    NovelImportService webNovels,
    ReadingCatalogSearchService catalogSearch,
    ReadingSourceSettingsStore sourceSettings) : IAcquisitionRequestExecutor
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

        // Search every enabled Reading source before Usenet. Only a source explicitly marked as
        // PublicFullText may be imported automatically; previews, shops and reference-only results
        // remain discovery evidence and can never bypass the normal acquisition path.
        var publicCopy = await TryImportPublicCopyAsync(
            payload,
            cancellationToken);
        if (publicCopy is not null)
        {
            return publicCopy;
        }

        return await engine.ExecuteAsync(
            request,
            ReadingAcquisitionEngine.ToTarget(MediaAcquisitionKind.LightNovel, payload),
            cancellationToken);
    }

    private async Task<AcquisitionExecution?> TryImportPublicCopyAsync(
        ReadingRequestPayload payload,
        CancellationToken cancellationToken)
    {
        ReadingSourceSettingsState settings;
        try
        {
            settings = await sourceSettings.LoadAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or
            InvalidDataException or
            UnauthorizedAccessException)
        {
            // Source configuration is optional enrichment for this acquisition attempt. Usenet
            // remains available even when reading-source settings cannot be loaded.
            return null;
        }

        var queries = new[] { payload.Title }
            .Concat(payload.Aliases ?? [])
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Select(query => query.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();

        foreach (var query in queries)
        {
            var outcome = await catalogSearch.SearchLightNovelsAsync(
                settings,
                query,
                limit: 12,
                cancellationToken);

            foreach (var candidate in outcome.Candidates)
            {
                if (!CanAutoImport(payload, candidate, settings))
                {
                    continue;
                }

                var definition = ReadingSourceCatalog.GetRequired(candidate.Provider);
                var sourceUrl = definition.DirectImportUrl!(candidate.ExternalId);
                try
                {
                    var workId = await webNovels.ImportWorkAsync(
                        sourceUrl,
                        cancellationToken);
                    return new AcquisitionExecution(
                        AcquisitionRequestStatus.Completed,
                        $"Imported a public copy from {definition.Name}.",
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
                    // A catalog result is evidence, not a guarantee that the full text is still
                    // reachable. Try the remaining candidates and ultimately the normal Usenet path.
                }
            }
        }

        return null;
    }

    public static bool CanAutoImport(
        ReadingRequestPayload payload,
        ReadingCatalogCandidate candidate,
        ReadingSourceSettingsState settings)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(settings);

        if (!candidate.IsPublicWebSource
            || !settings.IsEnabled(candidate.Provider)
            || !ReadingSourceCatalog.TryGet(candidate.Provider, out var definition)
            || !definition.SupportsDirectImport
            || definition.DirectImportUrl is null
            || !definition.IsValidExternalId(candidate.ExternalId))
        {
            return false;
        }

        // Never infer identity from a loose contains/prefix search result. At least one canonical
        // title or alias from the request must exactly match the result's title/native title after
        // the same normalization the Reading catalog uses for ranking.
        var names = new[] { payload.Title }
            .Concat(payload.Aliases ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name));

        return names.Any(name =>
            ReadingCatalogSearch.MatchScore(name, candidate) >= 1000);
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
