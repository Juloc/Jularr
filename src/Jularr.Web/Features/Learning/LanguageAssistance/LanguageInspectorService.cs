using System.Globalization;
using System.Text;
using Jularr.Web.Data;
using Jularr.Web.Features.Ai;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Learning.Courses;
using Jularr.Web.Features.Vocabulary;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Learning.LanguageAssistance;

/// <summary>
/// Server side of the shared language inspector used by the Anime player, the
/// readers and Learning modules. Every call resolves the capabilities of the
/// inspected source through <see cref="LearningModuleResolver"/>; card state
/// changes go through <see cref="LearningService"/> and context anchors
/// through <see cref="LearningCourseStore"/>.
/// </summary>
public sealed class LanguageInspectorService(
    AppDbContext db,
    LearningService learning,
    LanguageTextAnalyzer analyzer,
    AiSentenceExplanationService explanations,
    IInstanceModuleService? instanceModules = null)
{
    public const int MaxTextLength = 500;
    public const int MaxSentenceLength = 500;
    private const int MaxLanguageTagLength = 16;
    private const int MaxCanonicalLength = 300;

    private string ProfileId => learning.ProfileId;

    public async Task<LanguageInspection> InspectAsync(
        LanguageInspectRequest request,
        CancellationToken cancellationToken)
    {
        var text = Required(request.Text, MaxTextLength, "Text");
        var source = await ResolveSourceAsync(request.Context, cancellationToken);
        var availability = source.Availability;
        if (!availability.Any)
        {
            throw new LanguageAssistanceDeniedException(
                "Language tools are off for this content.");
        }

        if (!analyzer.CanAnalyze(source.Language))
        {
            throw new JapaneseAnalysisUnavailableException("Japanese text analysis is not available on this server.");
        }

        var analyzed = analyzer.Analyze(text, source.Language);
        var states = availability.Vocabulary
            ? await LoadStatesAsync(source.Language, analyzed, cancellationToken)
            : new Dictionary<string, UserTermState>(StringComparer.Ordinal);

        var tokens = analyzed
            .Select(token => new LanguageInspectorToken(
                token.Surface,
                token.Canonical,
                availability.Readings ? token.Reading : null,
                availability.Lookup ? token.Meaning : null,
                availability.Lookup ? token.MeaningLanguage : null,
                token.Interactive,
                token.Canonical is { } canonical && states.TryGetValue(canonical, out var state)
                    ? LanguageWordStates.Key(state)
                    : null))
            .ToArray();

        var explanationSupported = SentenceExplanationSupport.Supports(source.Language);
        AiSentenceExplanation? cached = null;
        if (availability.Explanations && explanationSupported)
        {
            cached = await explanations.GetCachedAsync(
                source.Sentence ?? text,
                cancellationToken);
        }

        return new LanguageInspection(
            source.Language,
            text,
            source.Sentence,
            LanguageInspectorFeatures.From(availability, explanationSupported),
            tokens,
            cached);
    }

    /// <summary>
    /// Save, Learn, Known or Ignore a word. Saving or learning from a source
    /// records the source context once per unit, source and position.
    /// </summary>
    public async Task<LanguageWordState> SetStateAsync(
        LanguageWordStateRequest request,
        CancellationToken cancellationToken)
    {
        if (!LanguageWordStates.TryParse(request.State, out var state))
        {
            throw new ArgumentException("Unknown word state.", nameof(request));
        }

        var source = await ResolveSourceAsync(request.Context, cancellationToken);
        var canonical = Required(request.Text, MaxCanonicalLength, "Text")
            .Normalize(LearningLanguageToolkitRegistry.Supports(
                source.Language,
                LearningLanguageCapability.Readings)
                ? NormalizationForm.FormKC
                : NormalizationForm.FormC);
        if (!canonical.Any(char.IsLetter))
        {
            throw new ArgumentException("Only words can be saved.", nameof(request));
        }

        if (!source.Availability.CanSave)
        {
            throw new LanguageAssistanceDeniedException(
                "Vocabulary is off for this content.");
        }

        if (state == UserTermState.Learning && !source.Availability.CanLearn)
        {
            throw new LanguageAssistanceDeniedException(
                "Reviews are off for this content.");
        }

        var term = await EnsureTermAsync(source.Language, canonical, cancellationToken);
        await learning.SetStateAsync(term.Id, state, cancellationToken);

        var recorded = false;
        if (state is UserTermState.Saved or UserTermState.Learning
            && source.Anchor is { } anchor)
        {
            var unitId = await db.LearningUnits
                .AsNoTracking()
                .Where(x => x.TermId == term.Id)
                .Select(x => x.Id)
                .SingleAsync(cancellationToken);

            await new LearningCourseStore(db).AddContextAsync(
                ProfileId,
                unitId,
                anchor.ToContext(source.Language, source.Sentence ?? canonical),
                cancellationToken);
            recorded = true;
        }

        return new LanguageWordState(
            canonical,
            source.Language,
            LanguageWordStates.Key(state),
            recorded);
    }

    /// <summary>
    /// Optional AI explanation of a sentence. Explanations are generated once
    /// and served from the shared explanation cache afterwards.
    /// </summary>
    public async Task<AiSentenceExplanation> ExplainAsync(
        LanguageExplainRequest request,
        CancellationToken cancellationToken)
    {
        var sentence = Required(request.Sentence, MaxSentenceLength, "Sentence");
        var source = await ResolveSourceAsync(request.Context, cancellationToken);
        if (!source.Availability.Explanations)
        {
            throw new LanguageAssistanceDeniedException(
                "Sentence explanations are off for this content.");
        }

        if (!SentenceExplanationSupport.Supports(source.Language))
        {
            throw new InvalidOperationException(
                "Sentence explanations are available for Japanese text.");
        }

        return await explanations.ExplainAsync(sentence, cancellationToken);
    }

    private async Task<ResolvedSource> ResolveSourceAsync(
        LanguageInspectContext? context,
        CancellationToken cancellationToken)
    {
        if (context is null
            || !LearningLanguageTag.TryNormalize(context.Language, out var language)
            || language.Length > MaxLanguageTagLength)
        {
            throw new ArgumentException("A valid content language is required.", nameof(context));
        }

        var sentence = Optional(context.Sentence, MaxSentenceLength);
        LanguageSourceAnchor? anchor = null;

        if (!string.IsNullOrWhiteSpace(context.SourceType))
        {
            if (!LanguageSourceAnchor.TryParseType(context.SourceType, out var type))
            {
                throw new ArgumentException("Unknown source type.", nameof(context));
            }

            if (!Guid.TryParse(context.ContentKey, out var contentId))
            {
                throw new ArgumentException("A content key is required for this source.", nameof(context));
            }

            var workKey = await FindWorkKeyAsync(type, contentId, cancellationToken)
                ?? throw new KeyNotFoundException("The inspected content does not exist.");

            anchor = new LanguageSourceAnchor(
                type,
                workKey,
                contentId,
                new LanguageSourcePosition(
                    context.CueStartMs,
                    context.Paragraph,
                    context.Page,
                    context.Region));
        }

        var availability = await new LearningModuleResolver(db, instanceModules).ResolveAssistanceAsync(
            ProfileId,
            anchor?.Scope,
            surface: null,
            cancellationToken);

        return new ResolvedSource(language, sentence, anchor, availability);
    }

    /// <summary>The work of a content item, taken from the library rather than the client.</summary>
    private async Task<string?> FindWorkKeyAsync(
        LanguageSourceType type,
        Guid contentId,
        CancellationToken cancellationToken)
    {
        switch (type)
        {
            case LanguageSourceType.Anime:
                var animeId = await db.Episodes
                    .AsNoTracking()
                    .Where(x => x.Id == contentId)
                    .Select(x => (Guid?)x.AnimeId)
                    .SingleOrDefaultAsync(cancellationToken);
                return animeId?.ToString("D");

            case LanguageSourceType.Novel:
            case LanguageSourceType.Book:
                var workId = await db.NovelChapters
                    .AsNoTracking()
                    .Where(x => x.Id == contentId)
                    .Select(x => (Guid?)x.WorkId)
                    .SingleOrDefaultAsync(cancellationToken);
                return workId?.ToString("D");

            case LanguageSourceType.Manga:
                var chapterKey = contentId.ToString("D");
                var seriesId = await db.Database
                    .SqlQuery<string>(
                        $"""SELECT "SeriesId" AS "Value" FROM "MangaChapters" WHERE "Id" = {chapterKey}""")
                    .FirstOrDefaultAsync(cancellationToken);
                return Guid.TryParse(seriesId, out var series) ? series.ToString("D") : null;

            default:
                return null;
        }
    }

    private async Task<Dictionary<string, UserTermState>> LoadStatesAsync(
        string language,
        IReadOnlyList<LanguageTextToken> tokens,
        CancellationToken cancellationToken)
    {
        var canonicals = tokens
            .Where(x => x.Interactive && x.Canonical is not null)
            .Select(x => x.Canonical!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (canonicals.Length == 0)
        {
            return new Dictionary<string, UserTermState>(StringComparer.Ordinal);
        }

        var rows = await (
            from term in db.Terms.AsNoTracking()
            join state in LearningQueries.TermStates(db, ProfileId) on term.Id equals state.TermId
            where term.Language == language && canonicals.Contains(term.Canonical)
            select new { term.Canonical, state.State })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(x => x.Canonical, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First().State, StringComparer.Ordinal);
    }

    /// <summary>
    /// Words saved from any source join the shared lexical catalog so every
    /// surface (subtitles, readers, Vocabulary) sees the same card state.
    /// </summary>
    private async Task<Term> EnsureTermAsync(
        string language,
        string canonical,
        CancellationToken cancellationToken)
    {
        var existing = await db.Terms
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Language == language && x.Canonical == canonical,
                cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var (reading, meaning) = analyzer.LookUp(canonical, language);
        var term = new Term
        {
            Language = language,
            Canonical = canonical,
            Reading = reading,
            Meaning = meaning
        };

        db.Terms.Add(term);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return term;
        }
        catch (DbUpdateException)
        {
            db.Entry(term).State = EntityState.Detached;
            return await db.Terms
                .AsNoTracking()
                .SingleAsync(
                    x => x.Language == language && x.Canonical == canonical,
                    cancellationToken);
        }
    }

    private static string Required(string? value, int maxLength, string name)
    {
        var cleaned = Optional(value, maxLength);
        return cleaned
            ?? throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"{name} is required."),
                name);
    }

    private static string? Optional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Text can have at most {maxLength} characters."));
        }

        return trimmed;
    }

    private sealed record ResolvedSource(
        string Language,
        string? Sentence,
        LanguageSourceAnchor? Anchor,
        LanguageAssistanceAvailability Availability);
}
