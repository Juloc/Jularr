using System.Globalization;
using System.Text;
using Jularr.Web.Data;
using Jularr.Web.Features.Ai;
using Jularr.Web.Features.Learning.Courses;
using Jularr.Web.Features.Learning.LanguageAssistance;
using Jularr.Web.Features.Progress;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Learning.Sentences;

public enum SentencePracticeMode
{
    /// <summary>The studied word is blanked out; reveal the full sentence.</summary>
    Cloze = 1,

    /// <summary>Read the full sentence, then reveal the word meaning and explanation.</summary>
    Comprehension = 2,

    /// <summary>Anime sentences: listen first, then reveal the text.</summary>
    Listening = 3
}

public static class SentencePracticeModes
{
    public static SentencePracticeMode Parse(string? value) =>
        Enum.TryParse<SentencePracticeMode>(value, ignoreCase: true, out var mode)
            && Enum.IsDefined(mode)
            && !int.TryParse(value, out _)
                ? mode
                : SentencePracticeMode.Cloze;

    public static string Key(SentencePracticeMode mode) =>
        mode.ToString().ToLowerInvariant();
}

/// <summary>Where a practice sentence comes from, with a link back into the content.</summary>
public sealed record SentencePracticeSource(
    LanguageSourceType Type,
    Guid ContentId,
    LanguageSourcePosition Position,
    string Title,
    string? Location,
    string Url);

public sealed record SentencePracticeItem(
    SentencePracticeSource Source,
    string Language,
    string Text,
    string? Cloze,
    string? TargetCanonical,
    string? TargetReading,
    string? TargetMeaning,
    UserTermState? TargetState,
    IReadOnlyList<LanguageTextToken> Tokens,
    AiSentenceExplanation? Explanation);

/// <summary>
/// Sentence practice module. Sentences come from content the profile actually
/// consumed and prefer words it Saved or is Learning:
/// <list type="number">
/// <item>contexts the profile recorded when saving a word in the player or a reader;</item>
/// <item>anime subtitle sentences of tracked words, episodes the profile watched first;</item>
/// <item>without tracked words, short subtitle sentences, watched episodes first.</item>
/// </list>
/// AI explanations are never generated here; cached ones are attached when requested.
/// </summary>
public sealed class SentencePracticeService(
    AppDbContext db,
    LanguageTextAnalyzer analyzer,
    AiSentenceExplanationService explanations)
{
    public const int MaxLimit = 50;
    private const string JapaneseLanguage = "ja";

    public async Task<IReadOnlyList<SentencePracticeItem>> LoadAsync(
        string profileId,
        SentencePracticeMode mode,
        int limit,
        bool withCachedExplanations,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        var candidates = new List<Candidate>();
        candidates.AddRange(await LoadContextCandidatesAsync(profileId, limit, cancellationToken));
        candidates.AddRange(await LoadSubtitleCandidatesAsync(profileId, limit, cancellationToken));

        var result = Build(candidates, mode, limit);
        if (result.Count == 0 && mode != SentencePracticeMode.Cloze)
        {
            result = Build(
                await LoadFallbackCandidatesAsync(profileId, limit, cancellationToken),
                mode,
                limit);
        }

        if (!withCachedExplanations)
        {
            return result;
        }

        var explained = new List<SentencePracticeItem>(result.Count);
        foreach (var item in result)
        {
            explained.Add(SentenceExplanationSupport.Supports(item.Language)
                ? item with
                {
                    Explanation = await explanations.GetCachedAsync(item.Text, cancellationToken)
                }
                : item);
        }

        return explained;
    }

    /// <summary>Sentences the profile recorded itself while saving words from any source.</summary>
    private async Task<IReadOnlyList<Candidate>> LoadContextCandidatesAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from context in db.LearningContexts.AsNoTracking()
            join card in LearningQueries.WordCards(db, profileId) on context.UnitId equals card.UnitId
            join course in db.LearningCourses.AsNoTracking() on card.CourseId equals course.Id
            where context.ProfileId == profileId
                && course.IsEnabled
                && course.SentencePracticeEnabled
                && course.SourceLanguage == context.LanguageTag
                && (card.State == UserTermState.Learning || card.State == UserTermState.Saved)
            orderby card.State == UserTermState.Learning ? 0 : 1,
                context.CreatedAt descending
            select new
            {
                context.Id,
                context.UnitId,
                context.SourceType,
                context.SourceKey,
                context.PositionKey,
                context.LanguageTag,
                context.Text,
                card.State,
                course.TargetLanguage
            })
            .Take(limit * 4)
            .ToListAsync(cancellationToken);

        rows = rows.DistinctBy(x => x.Id).ToList();
        if (rows.Count == 0)
        {
            return [];
        }

        var unitIds = rows.Select(x => x.UnitId).Distinct().ToArray();
        var variants = (await db.LearningVariants
                .AsNoTracking()
                .Where(x => unitIds.Contains(x.UnitId))
                .ToListAsync(cancellationToken))
            .ToLookup(x => x.UnitId);

        var anchors = new Dictionary<Guid, SourceAnchor>();
        foreach (var row in rows)
        {
            if (LanguageSourceAnchor.TryParseStored(
                    row.SourceType,
                    row.SourceKey,
                    row.PositionKey,
                    out var type,
                    out var contentId,
                    out var position))
            {
                anchors[row.Id] = new SourceAnchor(type, contentId, position);
            }
        }

        var sources = await DescribeSourcesAsync(anchors.Values.Distinct().ToArray(), cancellationToken);

        var result = new List<Candidate>(anchors.Count);
        foreach (var row in rows)
        {
            if (!anchors.TryGetValue(row.Id, out var anchor)
                || !sources.TryGetValue(anchor, out var source))
            {
                continue;
            }

            var word = Pick(variants[row.UnitId], row.LanguageTag);
            var meaning = Pick(variants[row.UnitId], row.TargetLanguage);
            result.Add(new Candidate(
                source,
                row.LanguageTag,
                row.Text,
                word?.Text,
                word?.Reading,
                meaning?.Text,
                row.State));
        }

        return result;
    }

    /// <summary>Episodes with any canonical progress for the profile: the content it actually consumed.</summary>
    private async Task<Guid[]> LoadConsumedEpisodeIdsAsync(
        string profileId,
        CancellationToken cancellationToken) =>
        [.. (await new VideoProgressService(db).GetLegacyEpisodeProgressAsync(profileId, null, cancellationToken)).Select(x => x.EpisodeId)];

    /// <summary>Anime subtitle sentences of words the profile tracks.</summary>
    private async Task<IReadOnlyList<Candidate>> LoadSubtitleCandidatesAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken)
    {
        var consumedEpisodeIds = await LoadConsumedEpisodeIdsAsync(profileId, cancellationToken);
        var rows = await (
            from termState in LearningQueries.TermStates(db, profileId)
            join term in db.Terms.AsNoTracking() on termState.TermId equals term.Id
            join episodeTerm in db.EpisodeTerms.AsNoTracking() on term.Id equals episodeTerm.TermId
            join episode in db.Episodes.AsNoTracking() on episodeTerm.EpisodeId equals episode.Id
            join anime in db.Anime.AsNoTracking() on episode.AnimeId equals anime.Id
            join track in db.SubtitleTracks.AsNoTracking() on episode.Id equals track.EpisodeId
            join cue in db.SubtitleCues.AsNoTracking() on track.Id equals cue.SubtitleTrackId
            let consumed = consumedEpisodeIds.Contains(episode.Id)
            let active = termState.State == UserTermState.Learning || termState.State == UserTermState.Saved
            where termState.SentencePracticeEnabled
                && (active || termState.State == UserTermState.Known)
                && track.Language == term.Language
                && cue.StartMs == episodeTerm.FirstCueStartMs
            orderby (active ? 0 : 2) + (consumed ? 0 : 1),
                episodeTerm.Occurrences descending,
                track.ImportedAt descending
            select new
            {
                EpisodeId = episode.Id,
                AnimeTitle = anime.Title,
                episode.SeasonNumber,
                episode.Number,
                cue.StartMs,
                cue.Text,
                term.Language,
                term.Canonical,
                term.Reading,
                term.Meaning,
                termState.State
            })
            .Take(Math.Max(80, limit * 10))
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new Candidate(
                AnimeSource(row.EpisodeId, row.AnimeTitle, row.SeasonNumber, row.Number, row.StartMs),
                row.Language,
                row.Text,
                row.Canonical,
                row.Reading,
                row.Meaning,
                row.State))
            .ToArray();
    }

    /// <summary>
    /// Short subtitle sentences when the profile tracks no word yet, in the
    /// content languages of the profile's enabled courses. A profile without
    /// any enabled course falls back to Japanese, the existing canonical
    /// default rather than a language check on the content itself.
    /// </summary>
    private async Task<IReadOnlyList<Candidate>> LoadFallbackCandidatesAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken)
    {
        var languages = await db.LearningCourses
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId && x.IsEnabled)
            .Select(x => x.SourceLanguage)
            .Distinct()
            .ToArrayAsync(cancellationToken);

        if (languages.Length == 0)
        {
            languages = [JapaneseLanguage];
        }

        var consumedEpisodeIds = await LoadConsumedEpisodeIdsAsync(profileId, cancellationToken);
        var rows = await (
            from track in db.SubtitleTracks.AsNoTracking()
            join cue in db.SubtitleCues.AsNoTracking() on track.Id equals cue.SubtitleTrackId
            join episode in db.Episodes.AsNoTracking() on track.EpisodeId equals episode.Id
            join anime in db.Anime.AsNoTracking() on episode.AnimeId equals anime.Id
            let consumed = consumedEpisodeIds.Contains(episode.Id)
            where languages.Contains(track.Language)
                && cue.Text.Length >= 2
                && cue.Text.Length <= SentencePracticeText.MaxLength
            orderby consumed ? 0 : 1,
                cue.Text.Length,
                track.ImportedAt descending,
                anime.Title,
                episode.Number
            select new
            {
                EpisodeId = episode.Id,
                AnimeTitle = anime.Title,
                episode.SeasonNumber,
                episode.Number,
                cue.StartMs,
                cue.Text,
                track.Language
            })
            .Take(Math.Max(100, limit * 12))
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new Candidate(
                AnimeSource(row.EpisodeId, row.AnimeTitle, row.SeasonNumber, row.Number, row.StartMs),
                row.Language,
                row.Text,
                null,
                null,
                null,
                null))
            .ToArray();
    }

    private List<SentencePracticeItem> Build(
        IReadOnlyList<Candidate> candidates,
        SentencePracticeMode mode,
        int limit)
    {
        var result = new List<SentencePracticeItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (mode == SentencePracticeMode.Listening
                && candidate.Source.Type != LanguageSourceType.Anime)
            {
                continue;
            }

            var text = candidate.Text.Trim();
            if (!SentencePracticeText.IsSuitable(text, candidate.Language)
                || !seen.Add(candidate.Source.Type + ":" + candidate.Source.ContentId + ":" + text))
            {
                continue;
            }

            var tokens = analyzer.Analyze(text, candidate.Language);
            var cloze = BuildCloze(tokens, candidate.TargetCanonical, candidate.Language);
            if (mode == SentencePracticeMode.Cloze && cloze is null)
            {
                continue;
            }

            result.Add(new SentencePracticeItem(
                candidate.Source,
                candidate.Language,
                string.Concat(tokens.Select(x => x.Surface)),
                cloze,
                candidate.TargetCanonical,
                candidate.TargetReading,
                candidate.TargetMeaning,
                candidate.TargetState,
                tokens,
                null));

            if (result.Count >= limit)
            {
                break;
            }
        }

        return result;
    }

    public static string? BuildCloze(
        IReadOnlyList<LanguageTextToken> tokens,
        string? targetCanonical,
        string language)
    {
        if (string.IsNullOrWhiteSpace(targetCanonical))
        {
            return null;
        }

        var japanese = LearningLanguageToolkitRegistry.Supports(language, LearningLanguageCapability.Readings);
        var comparison = japanese
            ? StringComparison.Ordinal
            : StringComparison.CurrentCultureIgnoreCase;
        var builder = new StringBuilder();
        var replaced = false;

        foreach (var token in tokens)
        {
            if (!replaced
                && token.Interactive
                && (string.Equals(token.Canonical, targetCanonical, comparison)
                    || string.Equals(token.Surface, targetCanonical, comparison)))
            {
                builder.Append(japanese ? "＿＿" : "____");
                replaced = true;
            }
            else
            {
                builder.Append(token.Surface);
            }
        }

        return replaced ? builder.ToString() : null;
    }

    private async Task<Dictionary<SourceAnchor, SentencePracticeSource>> DescribeSourcesAsync(
        IReadOnlyList<SourceAnchor> anchors,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<SourceAnchor, SentencePracticeSource>();

        var episodeIds = anchors
            .Where(x => x.Type == LanguageSourceType.Anime)
            .Select(x => x.ContentId)
            .Distinct()
            .ToArray();
        var episodes = episodeIds.Length == 0
            ? []
            : await (
                from episode in db.Episodes.AsNoTracking()
                join anime in db.Anime.AsNoTracking() on episode.AnimeId equals anime.Id
                where episodeIds.Contains(episode.Id)
                select new { episode.Id, anime.Title, episode.SeasonNumber, episode.Number })
                .ToDictionaryAsync(x => x.Id, cancellationToken);

        var chapterIds = anchors
            .Where(x => x.Type is LanguageSourceType.Book or LanguageSourceType.Novel)
            .Select(x => x.ContentId)
            .Distinct()
            .ToArray();
        var chapters = chapterIds.Length == 0
            ? []
            : await (
                from chapter in db.NovelChapters.AsNoTracking()
                join work in db.NovelWorks.AsNoTracking() on chapter.WorkId equals work.Id
                where chapterIds.Contains(chapter.Id)
                select new { chapter.Id, WorkTitle = work.MetadataTitle ?? work.Title, chapter.Title })
                .ToDictionaryAsync(x => x.Id, cancellationToken);

        foreach (var anchor in anchors)
        {
            var (type, contentId, position) = anchor;
            SentencePracticeSource? source = type switch
            {
                LanguageSourceType.Anime when episodes.TryGetValue(contentId, out var episode) =>
                    AnimeSource(
                        contentId,
                        episode.Title,
                        episode.SeasonNumber,
                        episode.Number,
                        position.CueStartMs ?? 0),
                LanguageSourceType.Book when chapters.TryGetValue(contentId, out var chapter) =>
                    new SentencePracticeSource(type, contentId, position, chapter.WorkTitle, chapter.Title, $"/Books/Read/{contentId:D}"),
                LanguageSourceType.Novel when chapters.TryGetValue(contentId, out var chapter) =>
                    new SentencePracticeSource(type, contentId, position, chapter.WorkTitle, chapter.Title, $"/Novels/Read/{contentId:D}"),
                LanguageSourceType.Manga =>
                    new SentencePracticeSource(
                        type,
                        contentId,
                        position,
                        "Manga",
                        position.Page is { } page ? page.ToString(CultureInfo.InvariantCulture) : null,
                        $"/Manga/Read/{contentId:D}"),
                _ => null
            };

            if (source is not null)
            {
                result[anchor] = source;
            }
        }

        return result;
    }

    private static SentencePracticeSource AnimeSource(
        Guid episodeId,
        string animeTitle,
        int seasonNumber,
        int episodeNumber,
        int cueStartMs) =>
        new(
            LanguageSourceType.Anime,
            episodeId,
            new LanguageSourcePosition(CueStartMs: cueStartMs),
            string.Create(
                CultureInfo.InvariantCulture,
                $"{animeTitle} · S{seasonNumber:00}E{episodeNumber:00}"),
            FormatTimestamp(cueStartMs),
            string.Create(
                CultureInfo.InvariantCulture,
                $"/Library/Episode/{episodeId:D}?at={cueStartMs}"));

    private static string FormatTimestamp(int milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}");
    }

    private static LearningVariant? Pick(IEnumerable<LearningVariant> variants, string languageTag) =>
        variants
            .Where(x => x.LanguageTag == languageTag)
            .OrderBy(x => x.Role == LearningVariantRole.Primary ? 0 : 1)
            .ThenBy(x => x.CreatedAt)
            .FirstOrDefault();

    private sealed record SourceAnchor(
        LanguageSourceType Type,
        Guid ContentId,
        LanguageSourcePosition Position);

    private sealed record Candidate(
        SentencePracticeSource Source,
        string Language,
        string Text,
        string? TargetCanonical,
        string? TargetReading,
        string? TargetMeaning,
        UserTermState? TargetState);
}

/// <summary>Which content sentences are short and clean enough to practice.</summary>
public static class SentencePracticeText
{
    public const int MaxJapaneseLength = 90;
    public const int MaxLength = 200;

    public static bool IsSuitable(string? text, string language)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.Length < 2)
        {
            return false;
        }

        return LearningLanguageToolkitRegistry.Supports(language, LearningLanguageCapability.Readings)
            ? trimmed.Length <= MaxJapaneseLength && JapaneseScript.Contains(trimmed)
            : trimmed.Length <= MaxLength && trimmed.Any(char.IsLetter);
    }
}
