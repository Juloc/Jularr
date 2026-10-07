using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Selection;

namespace Jularr.Web.Features.ReadingAcquisition;

public enum ReadingReleaseFormat
{
    Unknown,
    Epub,
    Pdf,
    Cbz,
    Zip,
    Cbr
}

public sealed record ReadingReleaseInfo(
    ReadingReleaseFormat Format,
    int? VolumeNumber,
    double? ChapterStart,
    double? ChapterEnd,
    string? Language,
    bool IsCompleteOrBatch);

public sealed record ReadingAcquisitionTarget(
    MediaAcquisitionKind Kind,
    string Title,
    IReadOnlyList<string> Aliases,
    string? Author = null,
    int? RequestedVolume = null,
    double? RequestedChapterStart = null,
    double? RequestedChapterEnd = null,
    IReadOnlyList<string>? PreferredLanguages = null);

public sealed record RankedReadingRelease(
    ProwlarrReleaseCandidate Release,
    ReadingReleaseInfo Parsed,
    int Score,
    string? RejectedBecause)
{
    /// <summary>What the shared selection engine concluded: identity confidence, decision, the profile score and every reason.</summary>
    public CandidateEvaluation? Selection { get; init; }
}

/// <summary>Every release judged by the shared selection engine, best first, and in words why the best one wins.</summary>
public sealed record ReadingRanking(IReadOnlyList<RankedReadingRelease> Ranked, string? WinnerReason);

public sealed record ReadingUsenetSearchResult(
    IReadOnlyList<string> Queries,
    IReadOnlyList<RankedReadingRelease> Ranked,
    IReadOnlyList<IndexerSearchWarning> Warnings,
    bool UsedCategoryFallback)
{
    /// <summary>The complete search with its per-indexer outcomes, provenance and trace; Manual Search reports from it.</summary>
    public AcquisitionSearchResult? Search { get; init; }

    public string? WinnerReason { get; init; }

    public ProwlarrReleaseCandidate? Picked =>
        Ranked.FirstOrDefault(candidate => candidate.Score > 0)?.Release;

    public string FailureMessage =>
        Ranked.Count == 0
            ? Warnings.Count > 0
                ? $"No release found on the indexers ({Warnings[0].IndexerName}: {Warnings[0].Message})."
                : "No release found on the indexers."
            : "No suitable release matched the requested title, format, volume, chapter or language.";
}

public static partial class ReadingReleaseParser
{
    [GeneratedRegex(@"(?:^|[\s._\-\[\(])(?:vol(?:ume)?\.?\s*|v)(?<value>\d{1,3})(?:\b|[\s._\-\]\)])", RegexOptions.IgnoreCase)]
    private static partial Regex VolumeRegex();

    [GeneratedRegex(@"(?:^|[\s._\-\[\(])(?:ch(?:apter)?\.?\s*|c)(?<start>\d{1,4}(?:\.\d+)?)(?:\s*(?:-|–|—|to)\s*(?:ch(?:apter)?\.?\s*|c)?(?<end>\d{1,4}(?:\.\d+)?))?", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterRegex();

    [GeneratedRegex(@"(?:^|[\s._\-\[\(])(?<start>\d{1,3})\s*(?:-|–|—|to)\s*(?<end>\d{1,3})(?:\s*(?:vol(?:ume)?s?|v))?(?:\b|[\s._\-\]\)])", RegexOptions.IgnoreCase)]
    private static partial Regex BareRangeRegex();

    public static ReadingReleaseInfo Parse(string? title)
    {
        var value = title ?? string.Empty;
        var lower = value.ToLowerInvariant();
        var format = lower.Contains("epub", StringComparison.Ordinal)
            ? ReadingReleaseFormat.Epub
            : lower.Contains("cbz", StringComparison.Ordinal)
                ? ReadingReleaseFormat.Cbz
                : lower.Contains("cbr", StringComparison.Ordinal)
                    ? ReadingReleaseFormat.Cbr
                    : lower.Contains("pdf", StringComparison.Ordinal)
                        ? ReadingReleaseFormat.Pdf
                        : ContainsToken(lower, "zip")
                            ? ReadingReleaseFormat.Zip
                            : ReadingReleaseFormat.Unknown;

        int? volume = null;
        var volumeMatch = VolumeRegex().Match(value);
        if (volumeMatch.Success &&
            int.TryParse(volumeMatch.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedVolume))
        {
            volume = parsedVolume;
        }

        double? chapterStart = null;
        double? chapterEnd = null;
        var chapterMatch = ChapterRegex().Match(value);
        if (chapterMatch.Success &&
            double.TryParse(chapterMatch.Groups["start"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedChapter))
        {
            chapterStart = parsedChapter;
            chapterEnd = parsedChapter;
            if (chapterMatch.Groups["end"].Success &&
                double.TryParse(chapterMatch.Groups["end"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedEnd))
            {
                chapterEnd = parsedEnd;
            }
        }

        var language = DetectLanguage(lower);
        var completeOrBatch =
            ContainsToken(lower, "complete") ||
            ContainsToken(lower, "completed") ||
            ContainsToken(lower, "batch") ||
            lower.Contains("all volumes", StringComparison.Ordinal) ||
            lower.Contains("volumes 1-", StringComparison.Ordinal) ||
            lower.Contains("vol 1-", StringComparison.Ordinal);

        if (!completeOrBatch)
        {
            var range = BareRangeRegex().Match(value);
            completeOrBatch = range.Success &&
                int.TryParse(range.Groups["start"].Value, out var start) &&
                int.TryParse(range.Groups["end"].Value, out var end) &&
                end > start;
        }

        return new ReadingReleaseInfo(
            format,
            volume,
            chapterStart,
            chapterEnd,
            language,
            completeOrBatch);
    }

    private static string? DetectLanguage(string lower)
    {
        if (ContainsAnyToken(lower, "german", "deutsch", "ger", "de-de"))
        {
            return "de";
        }

        if (ContainsAnyToken(lower, "english", "eng", "en-us", "en-gb"))
        {
            return "en";
        }

        if (ContainsAnyToken(lower, "japanese", "jpn", "japanese raw", "raw", "ja-jp"))
        {
            return "ja";
        }

        return null;
    }

    private static bool ContainsAnyToken(string value, params string[] tokens) =>
        tokens.Any(token => ContainsToken(value, token));

    private static bool ContainsToken(string value, string token)
    {
        var index = value.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(value[index - 1]);
            var end = index + token.Length;
            var after = end >= value.Length || !char.IsLetterOrDigit(value[end]);
            if (before && after)
            {
                return true;
            }

            index = value.IndexOf(token, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}

public static class ReadingReleaseSelector
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "and", "or", "der", "die", "das", "und", "des", "den",
        "le", "la", "les", "de", "no", "to"
    };

    /// <summary>
    /// Every release judged for the target and ordered by the shared selection engine: identity and format gates first, then the format tier of
    /// the profile, then the preference points of the request (exact volume, batch, chapter, language order, author), then the shared tiebreaks.
    /// </summary>
    public static IReadOnlyList<RankedReadingRelease> Rank(
        IReadOnlyList<ProwlarrReleaseCandidate> releases,
        ReadingAcquisitionTarget target,
        QualityProfile? profile = null,
        ReleaseReliabilityLookup? reliability = null) =>
        Evaluate(releases, target, profile, reliability).Ranked;

    public static ReadingRanking Evaluate(
        IReadOnlyList<ProwlarrReleaseCandidate> releases,
        ReadingAcquisitionTarget target,
        QualityProfile? profile = null,
        ReleaseReliabilityLookup? reliability = null)
    {
        var judged = releases
            .GroupBy(release => release.Identity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => ToJudgement(group.First(), target), StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var selection = ReleaseSelectionEngine.Select(profile ?? ReadingQualityProfiles.For(target.Kind), new SelectionContext(now, now), [.. judged.Values.Select(item => item.Candidate)], reliability);
        return new ReadingRanking([.. selection.Ranked.Select(evaluation => ToRanked(evaluation, judged[evaluation.Candidate.Id]))], selection.WinnerReason);
    }

    public static RankedReadingRelease Judge(
        ProwlarrReleaseCandidate release,
        ReadingAcquisitionTarget target,
        QualityProfile? profile = null) =>
        Rank([release], target, profile)[0];

    private sealed record ReadingJudgement(ProwlarrReleaseCandidate Release, ReadingReleaseInfo Parsed, SelectionCandidate Candidate);

    private static ReadingJudgement ToJudgement(ProwlarrReleaseCandidate release, ReadingAcquisitionTarget target)
    {
        var parsed = ReadingReleaseParser.Parse(release.Title);
        var names = new[] { target.Title }
            .Concat(target.Aliases ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var preferredLanguages = target.PreferredLanguages?
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Select(NormalizeLanguage)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        string? safety = null;
        if (release.InternalDownloadUri is null)
        {
            safety = "no download link";
        }
        else if (release.Protocol is not null && !release.Protocol.Equals("usenet", StringComparison.OrdinalIgnoreCase))
        {
            safety = "not a Usenet release";
        }
        else if (target.Kind == MediaAcquisitionKind.Manga && parsed.Format is ReadingReleaseFormat.Epub or ReadingReleaseFormat.Pdf or ReadingReleaseFormat.Cbr)
        {
            safety = "format is not supported by the Manga importer";
        }
        else if (target.Kind == MediaAcquisitionKind.LightNovel && parsed.Format is ReadingReleaseFormat.Pdf or ReadingReleaseFormat.Cbz or ReadingReleaseFormat.Cbr)
        {
            safety = "format is not supported by the Light Novel importer";
        }
        else if (target.Kind is not (MediaAcquisitionKind.Manga or MediaAcquisitionKind.LightNovel))
        {
            safety = "unsupported reading media type";
        }

        var releaseChapterEnd = parsed.ChapterEnd ?? parsed.ChapterStart;
        ReleaseIdentityEvidence identity;
        if (!names.Any(name => TitleMatches(release.Title, name)))
        {
            identity = ReleaseIdentityEvidence.Conflict("TitleDoesNotMatch", "title does not match");
        }
        else if (target.RequestedVolume is { } requestedVolume && parsed.VolumeNumber is { } releaseVolume && requestedVolume != releaseVolume)
        {
            identity = ReleaseIdentityEvidence.Conflict("WrongVolume", $"volume {releaseVolume} does not match requested volume {requestedVolume}");
        }
        else if (target.RequestedChapterStart is { } requestedChapter && parsed.ChapterStart is { } releaseChapterStart && (requestedChapter < releaseChapterStart || requestedChapter > releaseChapterEnd))
        {
            identity = ReleaseIdentityEvidence.Conflict("WrongChapter", "chapter range does not contain the requested chapter");
        }
        else if (preferredLanguages.Length > 0 && parsed.Language is { } releaseLanguage && !preferredLanguages.Contains(releaseLanguage, StringComparer.OrdinalIgnoreCase))
        {
            identity = ReleaseIdentityEvidence.Conflict("LanguageNotAllowed", $"release language '{releaseLanguage}' is not allowed");
        }
        else if ((target.RequestedVolume is { } volume && parsed.VolumeNumber == volume)
                 || (target.RequestedChapterStart is { } chapter && parsed.ChapterStart is { } start && chapter >= start && chapter <= releaseChapterEnd))
        {
            identity = ReleaseIdentityEvidence.Exact("VolumeOrChapter", "The requested volume or chapter is named in the release.");
        }
        else
        {
            identity = ReleaseIdentityEvidence.Strong("Title", "The title matches.");
        }

        var context = 0;
        if (target.RequestedVolume is { } wantedVolume && parsed.VolumeNumber == wantedVolume)
        {
            context += 24;
        }
        else if (target.RequestedVolume is null && parsed.IsCompleteOrBatch)
        {
            context += 12;
        }

        if (target.RequestedChapterStart is { } wantedChapter && parsed.ChapterStart is { } chapterStart && wantedChapter >= chapterStart && wantedChapter <= releaseChapterEnd)
        {
            context += 20;
        }

        if (preferredLanguages.Length > 0 && parsed.Language is { } language)
        {
            var languageIndex = Array.FindIndex(preferredLanguages, item => item.Equals(language, StringComparison.OrdinalIgnoreCase));
            context += Math.Max(2, 12 - languageIndex * 3);
        }

        if (!string.IsNullOrWhiteSpace(target.Author) && Words(release.Title).Overlaps(Words(target.Author)))
        {
            context += 5;
        }

        // An oversized release for one volume costs storage: it only loses against an otherwise equal one.
        var cost = release.SizeBytes switch
        {
            > 2L * 1024 * 1024 * 1024 when target.Kind == MediaAcquisitionKind.LightNovel => 20,
            > 100L * 1024 * 1024 * 1024 when target.Kind == MediaAcquisitionKind.Manga => 10,
            _ => 0
        };
        var candidate = new SelectionCandidate(
            release.Identity,
            ReadingReleaseEvidenceParser.Instance.Parse(release.Title),
            release.SizeBytes,
            release.Indexer,
            release.Sources.FirstOrDefault()?.Priority ?? 0,
            release.PublishedAt,
            identity,
            SelectionCoverage.Single with { Cost = cost })
        {
            SafetyRejection = safety,
            ContextScore = context
        };
        return new ReadingJudgement(release, parsed, candidate);
    }

    private static RankedReadingRelease ToRanked(CandidateEvaluation evaluation, ReadingJudgement judged)
    {
        if (!evaluation.IsSelectable)
        {
            var because = evaluation.Reasons.FirstOrDefault(reason => reason.Kind == SelectionReasonKind.Safety)?.Detail
                          ?? (evaluation.Candidate.Identity.Confidence == IdentityConfidence.Conflict
                              ? evaluation.Candidate.Identity.Detail
                              : string.Join("; ", evaluation.Score?.RejectionReasons ?? []));
            return new RankedReadingRelease(judged.Release, judged.Parsed, 0, because) { Selection = evaluation };
        }

        // The displayed score keeps one scale: a base, a step per format tier and the request's preference points, minus the storage cost.
        var tierPoints = (3 - Math.Min(evaluation.QualityRank, 3)) * 10;
        return new RankedReadingRelease(judged.Release, judged.Parsed, Math.Max(1, 100 + tierPoints + evaluation.PreferenceScore - evaluation.Candidate.Coverage.Cost), null) { Selection = evaluation };
    }

    internal static bool TitleMatches(string releaseTitle, string expectedTitle)
    {
        var compactExpected = Compact(expectedTitle);
        var compactRelease = Compact(releaseTitle);
        if (compactExpected.Length >= 4 &&
            compactRelease.Contains(compactExpected, StringComparison.Ordinal))
        {
            return true;
        }

        var expectedWords = Words(expectedTitle);
        if (expectedWords.Count == 0)
        {
            return false;
        }

        var releaseWords = Words(releaseTitle);
        return expectedWords.All(releaseWords.Contains);
    }

    internal static HashSet<string> Words(string? value)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return words;
        }

        foreach (var part in value.Split(
                     [' ', '.', '_', '-', ':', ';', ',', '(', ')', '[', ']', '{', '}', '\'', '"', '!', '?', '&', '/', '\\', '+'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = new string(part
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
            if (normalized.Length > 1 && !StopWords.Contains(normalized))
            {
                words.Add(normalized);
            }
        }

        return words;
    }

    private static string Compact(string? value) =>
        string.Concat((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant));

    private static string NormalizeLanguage(string value)
    {
        var lower = value.Trim().ToLowerInvariant();
        if (lower.StartsWith("de", StringComparison.Ordinal))
        {
            return "de";
        }

        if (lower.StartsWith("en", StringComparison.Ordinal))
        {
            return "en";
        }

        if (lower.StartsWith("ja", StringComparison.Ordinal) ||
            lower.StartsWith("jp", StringComparison.Ordinal))
        {
            return "ja";
        }

        return lower;
    }
}

/// <summary>
/// Searches the indexers for one Manga or Light Novel target through the shared planner: the author + title (Light Novels), the
/// title with the requested volume or chapter and the aliases, in the reading categories and, when too little matches, once more
/// without a category.
/// </summary>
public static class ReadingUsenetSearch
{
    public static async Task<ReadingUsenetSearchResult> SearchAsync(
        IndexerSearchCoordinator indexers,
        ReadingAcquisitionTarget target,
        CancellationToken cancellationToken,
        SearchOptions? options = null,
        QualityProfile? profile = null,
        ReleaseReliabilityLookup? reliability = null)
    {
        var intent = new SearchIntent(target.Kind, target.Title)
        {
            Aliases = target.Aliases ?? [],
            Creator = target.Author,
            Volume = target.RequestedVolume,
            Chapter = target.RequestedChapterStart is { } chapter ? (decimal)chapter : null
        };
        var result = await indexers.SearchAsync(
            intent,
            (options ?? new SearchOptions()) with { UsableCount = releases => ReadingReleaseSelector.Rank(releases, target, profile, reliability).Count(ranked => ranked.Score > 0) },
            cancellationToken);
        var ranking = ReadingReleaseSelector.Evaluate(result.Releases, target, profile, reliability);
        return new ReadingUsenetSearchResult(
            [.. result.Trace.Select(line => line.QueryText).Distinct(StringComparer.OrdinalIgnoreCase)],
            ranking.Ranked,
            result.Warnings,
            result.Trace.Any(line => line.Stage == "any-category" && line.Results > 0))
        {
            Search = result,
            WinnerReason = ranking.WinnerReason
        };
    }
}
