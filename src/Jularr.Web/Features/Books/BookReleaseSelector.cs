using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Access;

namespace Jularr.Web.Features.Books;

/// <summary>How many words of the requested title and author a candidate's name repeats; the Manual Search score shows it.</summary>
public sealed record BookMatch(int MatchedTitleWords, int AuthorHits);

/// <summary>Judges and ranks the candidates of one book: EPUB first, then PDF, title words must match; a direct source's candidate is its own identity evidence.</summary>
public static class BookReleaseSelector
{
    private static readonly string[] UnsupportedFormats = ["mobi", "azw3", "azw", "djvu", "cbr", "cbz", "mp3", "m4b", "audiobook", "hörbuch"];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "and", "der", "die", "das", "und", "des", "le", "la", "les"
    };

    /// <summary>The search of one book: the catalog id lets the free-edition source find the exact edition, title and author drive every other source.</summary>
    public static MediaSearchPlan<BookMatch> Plan(string title, string? author, string? catalogId)
    {
        var titleWords = Words(SearchPlanner.MainTitle(title));
        var authorWords = Words(author);
        var intent = new SearchIntent(MediaAcquisitionKind.Book, title.Trim())
        {
            Creator = string.IsNullOrWhiteSpace(author) ? null : author.Trim(),
            ExternalIds = string.IsNullOrWhiteSpace(catalogId) ? new Dictionary<string, string>() : new Dictionary<string, string> { [BookIntent.CatalogKey] = catalogId }
        };
        return new MediaSearchPlan<BookMatch>(intent, release => Judge(release, titleWords, authorWords));
    }

    public static AcquisitionCandidate? Pick(IReadOnlyList<AcquisitionCandidate> releases, string title, string? author, QualityProfile? profile = null) =>
        Rank(releases, title, author, profile).FirstOrDefault(release => release.Score > 0)?.Release;

    /// <summary>Every release, best first; rejected releases (score 0) last, each with its reason.</summary>
    public static IReadOnlyList<RankedBookRelease> Rank(IReadOnlyList<AcquisitionCandidate> releases, string title, string? author, QualityProfile? profile = null, ReleaseReliabilityLookup? reliability = null)
    {
        // Identity is decided first and the shared selection engine orders what is left, so a custom profile can reject or prefer a
        // format, regex or scored term but can never make a release for another book eligible.
        var plan = Plan(title, author, null);
        var judged = releases.GroupBy(release => release.Identity, StringComparer.Ordinal).ToDictionary(group => group.Key, group => (Release: group.First(), Judgement: plan.Judge(group.First())), StringComparer.Ordinal);
        var selection = ReleaseSelectionEngine.Select(
            profile ?? BookQualityProfiles.CreateDefaultBook(),
            [.. judged.Values.Select(item => new SelectionCandidate(item.Release.Identity, item.Judgement.Parsed, item.Release.SizeBytes, item.Release.Indexer, item.Release.Sources.FirstOrDefault()?.Priority ?? 0, item.Release.PublishedAt, item.Judgement.Evidence, item.Judgement.Coverage) { SafetyRejection = item.Judgement.SafetyRejection })],
            reliability);
        return [.. selection.Ranked.Select(evaluation => ToRanked(new ReleaseEvaluation<BookMatch>(judged[evaluation.Candidate.Id].Release, judged[evaluation.Candidate.Id].Judgement.Parsed, judged[evaluation.Candidate.Id].Judgement.Match, evaluation)))];
    }

    /// <summary>The rows Manual Search lists for what the core searched and ranked.</summary>
    public static BookUsenetSearchResult ToResult(SearchEvaluation<BookMatch> search) =>
        new(
            [.. search.Search.Trace.Select(line => line.QueryText).Distinct(StringComparer.OrdinalIgnoreCase)],
            [.. search.Releases.Select(ToRanked)],
            search.Search.Warnings,
            search.Search.Trace.Any(line => line.Stage == "any-category" && line.Results > 0))
        {
            EveryIndexerFailed = search.Search.EveryIndexerFailed
        };

    private static ReleaseJudgement<BookMatch> Judge(AcquisitionCandidate release, IReadOnlyCollection<string> titleWords, IReadOnlyCollection<string> authorWords)
    {
        var words = Words(release.Title);
        var matchedTitle = titleWords.Count(words.Contains);
        var authorHits = authorWords.Count(words.Contains);
        var parsed = BookReleaseParser.Instance.Parse(release.Title);
        var match = new BookMatch(matchedTitle, authorHits);

        // A direct source found the candidate for this very request; what it offers is that book.
        if (release.Type == AcquisitionType.DirectImport)
        {
            var direct = release.Offer!.IdentityIsExact ? ReleaseIdentityEvidence.Exact("CatalogEdition", "The catalog edition that was requested.") : ReleaseIdentityEvidence.Strong("Title", "The title and author match.");
            return new ReleaseJudgement<BookMatch>(match, parsed, direct, SelectionCoverage.Single, null);
        }

        var formatWords = words.Where(word => UnsupportedFormats.Contains(word)).ToArray();
        var safety = release.InternalDownloadUri is null
            ? "no download link"
            : release.Protocol is not null && !release.Protocol.Equals("usenet", StringComparison.OrdinalIgnoreCase)
                ? "not a Usenet release"
                : !words.Contains("epub") && !words.Contains("pdf") && formatWords.Length > 0 ? $"{formatWords[0].ToUpperInvariant()}, not EPUB or PDF" : null;
        var identity = titleWords.Count == 0
            ? ReleaseIdentityEvidence.Conflict("EmptyTitle", "empty title")
            : matchedTitle < titleWords.Count
                ? ReleaseIdentityEvidence.Conflict("TitleDoesNotMatch", "title does not match")
                : authorHits > 0
                    ? ReleaseIdentityEvidence.Exact("TitleAndAuthor", "Title and author match.")
                    : ReleaseIdentityEvidence.Strong("Title", "The title matches.");

        // An oversized release for one book costs storage: it only loses against an otherwise equal one.
        return new ReleaseJudgement<BookMatch>(match, parsed, identity, SelectionCoverage.Single with { Cost = release.SizeBytes is > 200L * 1024 * 1024 ? 6 : 0 }, safety);
    }

    private static RankedBookRelease ToRanked(ReleaseEvaluation<BookMatch> evaluation)
    {
        var candidate = evaluation.Selection;
        var score = candidate.Score;
        if (!candidate.IsSelectable)
        {
            var because = candidate.Reasons.FirstOrDefault(reason => reason.Kind is SelectionReasonKind.Safety)?.Detail
                          ?? (candidate.Candidate.Identity.Confidence == IdentityConfidence.Conflict ? candidate.Candidate.Identity.Detail : string.Join("; ", score?.RejectionReasons ?? []));
            return new RankedBookRelease(evaluation.Candidate, 0, because)
            {
                Evaluation = evaluation,
                QualityKey = score?.QualityKey,
                QualityRank = score?.QualityRank ?? int.MaxValue,
                ScoreReasons = score?.ScoreReasons ?? []
            };
        }

        // The displayed score keeps its meaning: identity points plus the profile preference, minus the cost of a large release.
        var reasons = new List<string> { $"Title match +{10 + evaluation.Match.MatchedTitleWords}" };
        var total = 10 + evaluation.Match.MatchedTitleWords + score!.Score;
        if (evaluation.Match.AuthorHits > 0)
        {
            total += evaluation.Match.AuthorHits * 2;
            reasons.Add($"Author match +{evaluation.Match.AuthorHits * 2}");
        }

        reasons.Add($"Quality {score.QualityKey}");
        reasons.AddRange(score.ScoreReasons);
        if (candidate.Candidate.Coverage.Cost > 0)
        {
            total -= candidate.Candidate.Coverage.Cost;
            reasons.Add($"Large release -{candidate.Candidate.Coverage.Cost}");
        }

        return new RankedBookRelease(evaluation.Candidate, Math.Max(total, 1), null)
        {
            Evaluation = evaluation,
            QualityKey = score.QualityKey,
            QualityRank = score.QualityRank,
            ScoreReasons = reasons
        };
    }

    private static HashSet<string> Words(string? value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        foreach (var word in value.Split([' ', '.', '_', '-', ':', ',', '(', ')', '[', ']', '\'', '"', '!', '?', '&', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length > 1 && !StopWords.Contains(word))
            {
                result.Add(word.ToLowerInvariant());
            }
        }

        return result;
    }
}
