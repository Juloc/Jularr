using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.MediaMapping;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Web.Features.Manga;

public sealed partial class MangaAniListService(
    MangaRepository repository,
    IHttpClientFactory httpClientFactory,
    MediaMappingReviewStore? reviewStore = null,
    ReadingSegmentMappingStore? segmentMappings = null,
    // Stores the matched cover beside the series' files on the Manga library root (#581); null
    // (hosts that do not exercise artwork placement) keeps the provider cover URL.
    ReadingCoverArtwork? coverArtwork = null)
{
    private readonly ReadingSegmentMappingStore segmentMappingsStore =
        segmentMappings ??
        new ReadingSegmentMappingStore(
            NullLogger<ReadingSegmentMappingStore>.Instance);
    private const string SearchQuery = """
        query ($search: String!, $perPage: Int!) {
          Page(page: 1, perPage: $perPage) {
            media(search: $search, type: MANGA, isAdult: false) {
              id
              format
              isAdult
              title { romaji english native }
              description(asHtml: false)
              coverImage { extraLarge large }
              bannerImage
              status
              chapters
              volumes
              startDate { year }
            }
          }
        }
        """;

    private const string ByIdQuery = """
        query ($id: Int!) {
          Media(id: $id, type: MANGA) {
            id
            format
            isAdult
            title { romaji english native }
            description(asHtml: false)
            coverImage { extraLarge large }
            bannerImage
            status
            chapters
            volumes
            startDate { year }
          }
        }
        """;

    private const string SequenceQuery = """
        query ($id: Int!) {
          Media(id: $id, type: MANGA) {
            id
            format
            isAdult
            title { romaji english native }
            description(asHtml: false)
            coverImage { extraLarge large }
            bannerImage
            status
            chapters
            volumes
            startDate { year }
            relations {
              edges {
                relationType
                node {
                  id
                  type
                  format
                  isAdult
                  title { romaji english native }
                  description(asHtml: false)
                  coverImage { extraLarge large }
                  bannerImage
                  status
                  chapters
                  volumes
                  startDate { year }
                }
              }
            }
          }
        }
        """;

    public async Task<IReadOnlyList<MangaAniListCandidate>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var normalized = query.Trim();
        if (normalized.Length == 0)
        {
            return [];
        }

        using var document = await SendAsync(
            SearchQuery,
            new { search = normalized, perPage = 12 },
            cancellationToken);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Page", out var page) ||
            !page.TryGetProperty("media", out var media) ||
            media.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return media.EnumerateArray()
            .Select(Parse)
            .Where(x => x is not null)
            .Cast<MangaAniListCandidate>()
            .ToArray();
    }

    public async Task<AutomaticMediaMatchDecision> AutoMatchAsync(
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        var source = await repository.GetAutoMatchSourceAsync(
            seriesId,
            cancellationToken);

        if (source is null)
        {
            return new AutomaticMediaMatchDecision(
                AutomaticMediaMatchDisposition.None,
                null,
                0,
                0,
                ["Manga series was not found."]);
        }

        if (!string.IsNullOrWhiteSpace(source.MetadataExternalId))
        {
            await AutoMapSegmentsAsync(
                seriesId,
                source.MetadataExternalId,
                source.Title,
                cancellationToken);

            return new AutomaticMediaMatchDecision(
                AutomaticMediaMatchDisposition.None,
                null,
                0,
                0,
                ["Manga already has an explicit metadata match; reading segments were reconciled."]);
        }

        IReadOnlyList<MangaAniListCandidate> candidates;
        try
        {
            candidates = await SearchAsync(source.Title, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AutomaticMediaMatchDecision(
                AutomaticMediaMatchDisposition.None,
                null,
                0,
                0,
                ["AniList metadata request timed out."]);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            HttpRequestException or
            JsonException)
        {
            return new AutomaticMediaMatchDecision(
                AutomaticMediaMatchDisposition.None,
                null,
                0,
                0,
                ["AniList metadata is currently unavailable."]);
        }

        var decision = AutomaticMediaMatcher.Select(
            new AutomaticMediaMatchInput(
                source.Title,
                Format: "MANGA"),
            candidates.Select(candidate => new AutomaticMediaMatchCandidate(
                "anilist",
                candidate.ExternalId,
                candidate.Title,
                new[]
                {
                    candidate.Title,
                    candidate.NativeTitle ?? ""
                },
                candidate.StartYear,
                candidate.ChapterCount,
                candidate.Format)));

        if (decision.CanApply && decision.Candidate is not null)
        {
            try
            {
                await MatchAsync(
                    seriesId,
                    decision.Candidate.ExternalId,
                    cancellationToken);

                if (reviewStore is not null)
                {
                    await reviewStore.ResolveAsync(
                        "manga",
                        seriesId.ToString(),
                        "identity",
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var reviewDecision = decision with
                {
                    Disposition = AutomaticMediaMatchDisposition.Review,
                    Evidence = decision.Evidence
                        .Append("AniList metadata request timed out before the automatic match could be persisted.")
                        .ToArray()
                };
                await SaveIdentityReviewAsync(
                    source,
                    reviewDecision,
                    cancellationToken);
                return reviewDecision;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                HttpRequestException or
                JsonException)
            {
                var reviewDecision = decision with
                {
                    Disposition = AutomaticMediaMatchDisposition.Review,
                    Evidence = decision.Evidence
                        .Append(exception.Message)
                        .ToArray()
                };
                await SaveIdentityReviewAsync(
                    source,
                    reviewDecision,
                    cancellationToken);
                return reviewDecision;
            }
        }
        else if (decision.Candidate is not null)
        {
            await SaveIdentityReviewAsync(
                source,
                decision,
                cancellationToken);
        }

        return decision;
    }

    public async Task<MangaAniListCandidate?> GetAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(externalId, out var id) || id <= 0)
        {
            throw new InvalidOperationException("Invalid AniList manga ID.");
        }

        using var document = await SendAsync(
            ByIdQuery,
            new { id },
            cancellationToken);

        return document.RootElement.TryGetProperty("data", out var data) &&
               data.TryGetProperty("Media", out var media) &&
               media.ValueKind != JsonValueKind.Null
            ? Parse(media)
            : null;
    }

    public async Task MatchAsync(
        Guid seriesId,
        string externalId,
        CancellationToken cancellationToken)
    {
        var candidate = await GetAsync(externalId, cancellationToken)
            ?? throw new InvalidOperationException("AniList manga was not found.");

        if (coverArtwork is not null)
        {
            candidate = candidate with
            {
                CoverImageUrl = await coverArtwork.PersistProviderCoverAsync(
                    MediaAcquisitionKind.Manga,
                    seriesId,
                    candidate.CoverImageUrl,
                    cancellationToken)
            };
        }

        await repository.UpdateMetadataAsync(
            seriesId,
            candidate,
            cancellationToken);

        if (reviewStore is not null)
        {
            await reviewStore.ResolveAsync(
                "manga",
                seriesId.ToString(),
                "identity",
                cancellationToken);
        }

        await AutoMapSegmentsAsync(
            seriesId,
            candidate.ExternalId,
            candidate.Title,
            cancellationToken);
    }

    private async Task AutoMapSegmentsAsync(
        Guid seriesId,
        string externalId,
        string localTitle,
        CancellationToken cancellationToken)
    {
        if (await segmentMappingsStore.HasManualMappingsAsync(
                "manga",
                seriesId.ToString(),
                cancellationToken))
        {
            if (reviewStore is not null)
            {
                await reviewStore.ResolveAsync(
                    "manga",
                    seriesId.ToString(),
                    "reading-segments",
                    cancellationToken);
            }

            return;
        }

        LinearRelationSequenceResult<MangaAniListCandidate> sequence;
        try
        {
            sequence = await GetLinearSequenceAsync(
                externalId,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            HttpRequestException or
            JsonException or
            TaskCanceledException)
        {
            if (reviewStore is not null)
            {
                await reviewStore.UpsertAsync(
                    "manga",
                    seriesId.ToString(),
                    localTitle,
                    "reading-segments",
                    $"Automatic AniList segment reconciliation could not run: {exception.Message}",
                    [],
                    cancellationToken);
            }

            return;
        }

        if (!sequence.IsUnambiguous)
        {
            await segmentMappingsStore.ClearAutomaticAsync(
                "manga",
                seriesId.ToString(),
                cancellationToken);
            await SaveSegmentReviewAsync(
                seriesId,
                localTitle,
                sequence.Reason,
                sequence.Entries,
                cancellationToken);
            return;
        }

        var chapters = await repository.GetChaptersAsync(
            seriesId,
            cancellationToken);

        var plan = AutomaticReadingSegmentPlanner.Plan(
            chapters.Select(chapter => new LocalReadingChapter(
                chapter.Number,
                chapter.VolumeNumber)).ToArray(),
            sequence.Entries
                .Select(candidate => new RemoteReadingPart(
                    "anilist",
                    candidate.ExternalId,
                    candidate.Title,
                    candidate.ChapterCount ?? 0,
                    candidate.VolumeCount))
                .ToArray(),
            externalId);

        if (plan.NoMappingRequired)
        {
            await segmentMappingsStore.ClearAutomaticAsync(
                "manga",
                seriesId.ToString(),
                cancellationToken);

            if (reviewStore is not null)
            {
                await reviewStore.ResolveAsync(
                    "manga",
                    seriesId.ToString(),
                    "reading-segments",
                    cancellationToken);
            }

            return;
        }

        if (!plan.CanApply)
        {
            await segmentMappingsStore.ClearAutomaticAsync(
                "manga",
                seriesId.ToString(),
                cancellationToken);
            await SaveSegmentReviewAsync(
                seriesId,
                localTitle,
                plan.Reason,
                sequence.Entries,
                cancellationToken);
            return;
        }

        var mappings = plan.Segments
            .Select(segment => new ReadingMediaSegmentMapping(
                Guid.NewGuid(),
                "manga",
                seriesId.ToString(),
                segment.LocalChapterStart,
                segment.LocalChapterEnd,
                segment.RemoteChapterStart,
                segment.RemotePart.Provider,
                segment.RemotePart.ExternalId,
                segment.RemotePart.Title,
                segment.RemotePart.ChapterCount,
                segment.LocalVolumeStart,
                segment.LocalVolumeEnd,
                segment.RemoteVolumeStart,
                DateTimeOffset.UtcNow)
            {
                Source = "automatic"
            })
            .ToArray();

        var applied = await segmentMappingsStore.ReplaceAutomaticAsync(
            "manga",
            seriesId.ToString(),
            mappings,
            cancellationToken);

        if (reviewStore is null)
        {
            return;
        }

        if (applied)
        {
            await reviewStore.ResolveAsync(
                "manga",
                seriesId.ToString(),
                "reading-segments",
                cancellationToken);
        }
        else
        {
            await reviewStore.ResolveAsync(
                "manga",
                seriesId.ToString(),
                "reading-segments",
                cancellationToken);
        }
    }

    private async Task SaveSegmentReviewAsync(
        Guid seriesId,
        string localTitle,
        string reason,
        IReadOnlyList<MangaAniListCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (reviewStore is null)
        {
            return;
        }

        await reviewStore.UpsertAsync(
            "manga",
            seriesId.ToString(),
            localTitle,
            "reading-segments",
            reason,
            candidates.Select(candidate => new MediaMappingReviewCandidate(
                "anilist",
                candidate.ExternalId,
                candidate.Title,
                0,
                ["AniList PREQUEL/SEQUEL structure"],
                candidate.Format,
                candidate.StartYear,
                candidate.ChapterCount)).ToArray(),
            cancellationToken);
    }

    public Task<LinearRelationSequenceResult<MangaAniListCandidate>> GetLinearSequenceAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(externalId, out var id) || id <= 0)
        {
            return Task.FromResult(
                new LinearRelationSequenceResult<MangaAniListCandidate>(
                    [],
                    false,
                    "AniList manga ID is invalid."));
        }

        return LinearRelationSequence.ResolveAsync(
            id.ToString(),
            LoadSequenceNodeAsync,
            cancellationToken);
    }

    private async Task<LinearRelationNode<MangaAniListCandidate>?> LoadSequenceNodeAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(externalId, out var id) || id <= 0)
        {
            return null;
        }

        using var document = await SendAsync(
            SequenceQuery,
            new { id },
            cancellationToken);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Media", out var media) ||
            media.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var candidate = Parse(media);
        if (candidate is null)
        {
            return null;
        }

        var prequels = new List<string>();
        var sequels = new List<string>();

        if (media.TryGetProperty("relations", out var relations) &&
            relations.ValueKind == JsonValueKind.Object &&
            relations.TryGetProperty("edges", out var edges) &&
            edges.ValueKind == JsonValueKind.Array)
        {
            foreach (var edge in edges.EnumerateArray())
            {
                var relationType = ReadString(edge, "relationType");
                if (relationType is not ("PREQUEL" or "SEQUEL") ||
                    !edge.TryGetProperty("node", out var node) ||
                    node.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var related = Parse(node);
                if (related is null)
                {
                    continue;
                }

                if (relationType == "PREQUEL")
                {
                    prequels.Add(related.ExternalId);
                }
                else
                {
                    sequels.Add(related.ExternalId);
                }
            }
        }

        return new LinearRelationNode<MangaAniListCandidate>(
            candidate,
            prequels.Distinct(StringComparer.Ordinal).ToArray(),
            sequels.Distinct(StringComparer.Ordinal).ToArray());
    }

    private async Task SaveIdentityReviewAsync(
        MangaAutoMatchSource source,
        AutomaticMediaMatchDecision decision,
        CancellationToken cancellationToken)
    {
        if (reviewStore is null || decision.Candidate is null)
        {
            return;
        }

        var reason = decision.Disposition == AutomaticMediaMatchDisposition.Review
            ? $"AniList identity needs review: score {decision.Score}, runner-up {decision.RunnerUpScore}."
            : $"AniList identity confidence is too low for automatic matching: score {decision.Score}.";

        await reviewStore.UpsertAsync(
            "manga",
            source.SeriesId.ToString(),
            source.Title,
            "identity",
            reason,
            [
                new MediaMappingReviewCandidate(
                    decision.Candidate.Provider,
                    decision.Candidate.ExternalId,
                    decision.Candidate.PreferredTitle,
                    decision.Score,
                    decision.Evidence,
                    decision.Candidate.Format,
                    decision.Candidate.Year,
                    decision.Candidate.UnitCount)
            ],
            cancellationToken);
    }

    private async Task<JsonDocument> SendAsync(
        string query,
        object variables,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri("https://graphql.anilist.co/");
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        using var response = await client.PostAsJsonAsync(
            "",
            new { query, variables },
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"AniList returned HTTP {(int)response.StatusCode}.");
        }

        var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0)
        {
            var message = errors[0].TryGetProperty("message", out var element)
                ? element.GetString()
                : null;
            document.Dispose();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message)
                    ? "AniList returned an error."
                    : $"AniList: {message}");
        }

        return document;
    }

    private static MangaAniListCandidate? Parse(JsonElement media)
    {
        if (!media.TryGetProperty("id", out var idElement) ||
            !idElement.TryGetInt32(out var id))
        {
            return null;
        }

        if (media.TryGetProperty("isAdult", out var adult) &&
            adult.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        var format = ReadString(media, "format");
        if (string.Equals(format, "NOVEL", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var title = media.TryGetProperty("title", out var titles)
            ? titles
            : default;
        var preferred = FirstNonEmpty(
            ReadString(title, "english"),
            ReadString(title, "romaji"),
            ReadString(title, "native")) ?? $"AniList {id}";

        string? cover = null;
        if (media.TryGetProperty("coverImage", out var coverElement) &&
            coverElement.ValueKind == JsonValueKind.Object)
        {
            cover = FirstNonEmpty(
                ReadString(coverElement, "extraLarge"),
                ReadString(coverElement, "large"));
        }

        int? startYear = null;
        if (media.TryGetProperty("startDate", out var startDate) &&
            startDate.ValueKind == JsonValueKind.Object)
        {
            startYear = ReadInt(startDate, "year");
        }

        return new MangaAniListCandidate(
            id.ToString(),
            preferred,
            ReadString(title, "native"),
            NormalizeDescription(ReadString(media, "description")),
            cover,
            ReadString(media, "bannerImage"),
            ReadString(media, "status"),
            format,
            ReadInt(media, "chapters"),
            ReadInt(media, "volumes"),
            startYear);
    }

    private static string? NormalizeDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var decoded = WebUtility.HtmlDecode(HtmlTag().Replace(value, " "));
        var normalized = Whitespace().Replace(decoded, " ").Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static int? ReadInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static string? ReadString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
