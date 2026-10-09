using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Core;
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
    bool IsCompleteOrBatch,
    int? VolumeEnd = null)
{
    private bool Everything => VolumeNumber is null && ChapterStart is null && IsCompleteOrBatch;

    public bool HoldsVolume(int volume) => Everything || (VolumeNumber is { } first && volume >= first && volume <= (VolumeEnd ?? first));

    public bool HoldsChapter(double chapter) => Everything || (ChapterStart is { } first && chapter >= first && chapter <= (ChapterEnd ?? first));
}

// What a Work still lacks and already holds, as volume and chapter numbers; present only when the Work has a provider-identified structure. A held unit whose
// installed quality the profile still wants better (Upgrade*, with that quality) is wanted again, but only by a release that is better by the profile's upgrade policy.
public sealed record ReadingWant(
    IReadOnlyList<int> Volumes,
    IReadOnlyList<double> Chapters,
    IReadOnlyList<int> HeldVolumes,
    IReadOnlyList<double> HeldChapters)
{
    public IReadOnlyDictionary<int, string?> UpgradeVolumes { get; init; } = new Dictionary<int, string?>();

    public IReadOnlyDictionary<double, string?> UpgradeChapters { get; init; } = new Dictionary<double, string?>();

    public QualityProfile? Profile { get; init; }

    public bool IsEmpty => Volumes.Count == 0 && Chapters.Count == 0 && UpgradeVolumes.Count == 0 && UpgradeChapters.Count == 0;

    public bool HasMissing => Volumes.Count > 0 || Chapters.Count > 0;

    public int Total => Volumes.Count + Chapters.Count + UpgradeVolumes.Count + UpgradeChapters.Count;

    public (int Wanted, int Unwanted) Cover(ReadingReleaseInfo release, string? quality)
    {
        var (holds, contains) = Spans(release);
        var upgraded = UpgradeVolumes.Count(unit => holds(unit.Key) && Improves(unit.Value, quality)) + UpgradeChapters.Count(unit => contains(unit.Key) && Improves(unit.Value, quality));
        var held = HeldVolumes.Count(unit => holds(unit)) + HeldChapters.Count(unit => contains(unit));
        return (Volumes.Count(unit => holds(unit)) + Chapters.Count(unit => contains(unit)) + upgraded, Math.Max(0, held - upgraded));
    }

    // The installed quality of the first unit a release holds that is only wanted for an upgrade, or null when it holds none.
    public string? UpgradeTargetOf(ReadingReleaseInfo release)
    {
        var (holds, contains) = Spans(release);
        return UpgradeVolumes.Where(unit => holds(unit.Key)).Select(unit => unit.Value).Concat(UpgradeChapters.Where(unit => contains(unit.Key)).Select(unit => unit.Value)).FirstOrDefault();
    }

    public bool OverlapsUpgrade(ReadingReleaseInfo release)
    {
        var (holds, contains) = Spans(release);
        return UpgradeVolumes.Keys.Any(unit => holds(unit)) || UpgradeChapters.Keys.Any(unit => contains(unit));
    }

    private bool Improves(string? installed, string? candidate) => Profile is not null && UpgradePolicy.IsUpgrade(Profile, installed, candidate);

    private static (Func<int, bool> Holds, Func<double, bool> Contains) Spans(ReadingReleaseInfo release) => (release.HoldsVolume, release.HoldsChapter);
}

public sealed record ReadingAcquisitionTarget(
    MediaAcquisitionKind Kind,
    string Title,
    IReadOnlyList<string> Aliases,
    string? Author = null,
    int? RequestedVolume = null,
    double? RequestedChapterStart = null,
    double? RequestedChapterEnd = null,
    IReadOnlyList<string>? PreferredLanguages = null,
    ReadingWant? Want = null);

public static partial class ReadingReleaseParser
{
    [GeneratedRegex(@"(?:^|[\s._\-\[\(])(?:vol(?:ume)?s?\.?\s*|v)(?<value>\d{1,3})(?:\s*(?:-|–|—|to)\s*(?:vol(?:ume)?s?\.?\s*|v)?(?<end>\d{1,3})(?!\d))?(?:\b|[\s._\-\]\)])", RegexOptions.IgnoreCase)]
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
        int? volumeEnd = null;
        var volumeMatch = VolumeRegex().Match(value);
        if (volumeMatch.Success &&
            int.TryParse(volumeMatch.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedVolume))
        {
            volume = parsedVolume;
            if (volumeMatch.Groups["end"].Success &&
                int.TryParse(volumeMatch.Groups["end"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedVolumeEnd) &&
                parsedVolumeEnd > parsedVolume)
            {
                volumeEnd = parsedVolumeEnd;
            }
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
            volumeEnd is not null ||
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
            completeOrBatch,
            volumeEnd);
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

public static class ReadingReleaseJudge
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "and", "or", "der", "die", "das", "und", "des", "den",
        "le", "la", "les", "de", "no", "to"
    };

    // The one scale the Manga and Light Novel pages show: a base, a step per format tier and the request's preference points, minus the storage cost; 0 when refused.
    public static int DisplayScore(ReleaseEvaluation<ReadingReleaseInfo> evaluation) =>
        evaluation.Selection.IsSelectable && evaluation.Candidate.InternalDownloadUri is not null
            ? Math.Max(1, 100 + (3 - Math.Min(evaluation.Selection.QualityRank, 3)) * 10 + evaluation.Selection.PreferenceScore - evaluation.Selection.Candidate.Coverage.Cost)
            : 0;

    public static string? RejectedBecause(ReleaseEvaluation<ReadingReleaseInfo> evaluation) =>
        DisplayScore(evaluation) > 0
            ? null
            : evaluation.Selection.Reasons.FirstOrDefault(reason => reason.Kind == SelectionReasonKind.Safety)?.Detail
              ?? (evaluation.Selection.Candidate.Identity.Confidence == IdentityConfidence.Conflict
                  ? evaluation.Selection.Candidate.Identity.Detail
                  : string.Join("; ", evaluation.Selection.Score?.RejectionReasons ?? []));

    // The search facts and identity judge of one Manga or Light Novel target for the shared acquisition core.
    public static MediaSearchPlan<ReadingReleaseInfo> Plan(ReadingAcquisitionTarget target) =>
        new(
            new SearchIntent(target.Kind, target.Title)
            {
                Aliases = target.Aliases ?? [],
                Creator = target.Author,
                Volume = target.RequestedVolume ?? (target.Want is { Volumes: [var only], Chapters.Count: 0 } ? only : null),
                Chapter = target.RequestedChapterStart is { } chapter ? (decimal)chapter : null
            },
            release => Judge(release, target));

    public static ReleaseJudgement<ReadingReleaseInfo> Judge(AcquisitionCandidate release, ReadingAcquisitionTarget target)
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

        // A direct source found the candidate for this very request, and a public full-text copy holds every volume of the series.
        if (release.Type == AcquisitionType.DirectImport)
        {
            var direct = release.Offer!.IdentityIsExact ? ReleaseIdentityEvidence.Exact("PublicCopy", "A public full-text copy of the requested title.") : ReleaseIdentityEvidence.Strong("Title", "The title matches.");
            return new ReleaseJudgement<ReadingReleaseInfo>(parsed, ReadingReleaseEvidenceParser.Instance.Parse(release.Title), direct, SelectionCoverage.Single, null)
            {
                ContextScore = target.RequestedVolume is null ? 12 : 24
            };
        }

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
        var want = target.Want is { IsEmpty: false } known ? known : null;
        ReleaseIdentityEvidence identity;
        if (!names.Any(name => TitleMatches(release.Title, name)))
        {
            identity = ReleaseIdentityEvidence.Conflict("TitleDoesNotMatch", "title does not match");
        }
        else if (target.Want is { IsEmpty: true })
        {
            identity = ReleaseIdentityEvidence.Conflict("NothingMissing", "nothing is missing");
        }
        else if (want is not null && preferredLanguages.Length > 0 && parsed.Language is { } wantedLanguage && !preferredLanguages.Contains(wantedLanguage, StringComparer.OrdinalIgnoreCase))
        {
            identity = ReleaseIdentityEvidence.Conflict("LanguageNotAllowed", $"release language '{wantedLanguage}' is not allowed");
        }
        else if (want is not null)
        {
            identity = UnitIdentity(parsed, want);
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
        var coverage = SelectionCoverage.Single;
        if (want is not null)
        {
            // Covering more of what is missing wins, and every unit the library already holds is a download nobody needs.
            var (covered, unwanted) = want.Cover(parsed, ReadingReleaseEvidenceParser.QualityOf(parsed.Format));
            coverage = new SelectionCoverage(covered, want.Total, unwanted);
            context += Math.Min(covered, 12) * 6 - Math.Min(unwanted, 10) * 4;
        }
        else if (target.RequestedVolume is { } wantedVolume && parsed.VolumeNumber == wantedVolume)
        {
            context += 24;
        }
        else if (target.RequestedVolume is null && parsed.IsCompleteOrBatch)
        {
            context += 12;
        }

        if (want is null && target.RequestedChapterStart is { } wantedChapter && parsed.ChapterStart is { } chapterStart && wantedChapter >= chapterStart && wantedChapter <= releaseChapterEnd)
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
        return new ReleaseJudgement<ReadingReleaseInfo>(parsed, ReadingReleaseEvidenceParser.Instance.Parse(release.Title), identity, coverage with { Cost = cost }, safety)
        {
            ContextScore = context,
            Languages = parsed.Language is { } statedLanguage ? [statedLanguage] : null
        };
    }

    // Which of the wanted units a release holds: one that names no volume, chapter or batch cannot be told apart from any other, and one that holds
    // nothing that is missing is a download nobody needs.
    private static ReleaseIdentityEvidence UnitIdentity(ReadingReleaseInfo parsed, ReadingWant want)
    {
        if (parsed.VolumeNumber is null && parsed.ChapterStart is null && !parsed.IsCompleteOrBatch)
        {
            return ReleaseIdentityEvidence.Ambiguous("UnnamedUnit", "the release names no volume, chapter or batch");
        }

        var (covered, _) = want.Cover(parsed, ReadingReleaseEvidenceParser.QualityOf(parsed.Format));
        if (covered > 0)
        {
            return parsed.VolumeNumber is not null || parsed.ChapterStart is not null
                ? ReleaseIdentityEvidence.Exact("VolumeOrChapter", "The release names a volume or chapter that is wanted.")
                : ReleaseIdentityEvidence.Strong("CompleteSeries", "A complete set that holds wanted units.");
        }

        if (want.OverlapsUpgrade(parsed))
        {
            return ReleaseIdentityEvidence.Conflict("NotAnUpgrade", $"not better than the installed {want.UpgradeTargetOf(parsed) ?? "version"}");
        }

        return parsed.VolumeNumber is { } volume
            ? ReleaseIdentityEvidence.Conflict("WrongVolume", parsed.VolumeEnd is { } end ? $"volumes {volume}-{end} are not wanted" : $"volume {volume} is not wanted")
            : parsed.ChapterStart is { } chapter
                ? ReleaseIdentityEvidence.Conflict("WrongChapter", parsed.ChapterEnd is { } last && last != chapter ? $"chapters {chapter:0.##}-{last:0.##} are not wanted" : $"chapter {chapter:0.##} is not wanted")
                : ReleaseIdentityEvidence.Conflict("NothingWanted", "the set holds nothing that is missing");
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
