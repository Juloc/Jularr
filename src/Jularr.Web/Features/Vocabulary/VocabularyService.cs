using Jularr.Web.Data;
using Jularr.Web.Features.Learning.Courses;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Vocabulary;

/// <summary>
/// Builds the per-episode subtitle word catalog. Term extraction and
/// dictionary enrichment are resolved per subtitle track through the language
/// toolkit registry, so an episode's learning text is prepared in whichever
/// language its track actually carries rather than an assumed one; today that
/// is Japanese for every track the subtitle pipeline imports, which stays the
/// canonical default when nothing else is configured.
/// </summary>
public sealed class VocabularyService
{
    private readonly AppDbContext db;
    private readonly LearningLanguageToolkitRegistry toolkits;

    public VocabularyService(
        AppDbContext db,
        JapaneseTermExtractor extractor,
        JapaneseDictionary dictionary)
    {
        this.db = db;
        toolkits = new LearningLanguageToolkitRegistry(extractor, dictionary);
    }

    public async Task RebuildEpisodeAsync(Guid episodeId, CancellationToken cancellationToken)
    {
        var cues = await (
            from cue in db.SubtitleCues.AsNoTracking()
            join track in db.SubtitleTracks.AsNoTracking() on cue.SubtitleTrackId equals track.Id
            where track.EpisodeId == episodeId
            orderby cue.StartMs
            select new { track.Language, cue.Text, cue.StartMs })
            .ToListAsync(cancellationToken);

        foreach (var entry in db.ChangeTracker
                     .Entries<EpisodeTerm>()
                     .Where(x => x.Entity.EpisodeId == episodeId)
                     .ToArray())
        {
            entry.State = EntityState.Detached;
        }

        await db.EpisodeTerms
            .Where(x => x.EpisodeId == episodeId)
            .ExecuteDeleteAsync(cancellationToken);

        if (cues.Count == 0)
        {
            return;
        }

        var episodeTerms = new List<EpisodeTerm>();

        foreach (var track in cues.GroupBy(x => x.Language, StringComparer.Ordinal))
        {
            var language = track.Key;
            var toolkit = toolkits.Get(language);
            if (toolkit.TermExtractor is not { } extractor)
            {
                // The toolkit for this track's language has no term extractor;
                // never fake extraction for it.
                continue;
            }

            var aggregate = new Dictionary<string, TermAggregate>(StringComparer.Ordinal);

            try
            {
                foreach (var cue in track)
                {
                    foreach (var candidate in extractor.Extract(cue.Text))
                    {
                        if (aggregate.TryGetValue(candidate.Canonical, out var current))
                        {
                            aggregate[candidate.Canonical] = current with
                            {
                                Count = current.Count + 1,
                                Reading = PreferReading(current.Reading, candidate.Reading)
                            };
                        }
                        else
                        {
                            aggregate[candidate.Canonical] = new TermAggregate(
                                Count: 1,
                                FirstMs: cue.StartMs,
                                Reading: candidate.Reading);
                        }
                    }
                }
            }
            catch (JapaneseAnalysisUnavailableException)
            {
                // Vocabulary is derived state: a missing dictionary (already logged and shown on Admin > Health by the
                // morphology owner) must not fail the subtitle import; the episode is rebuilt on its next import or repair.
                continue;
            }

            if (aggregate.Count == 0)
            {
                continue;
            }

            var canonicalTerms = aggregate.Keys.ToArray();
            var terms = await db.Terms
                .Where(x => x.Language == language && canonicalTerms.Contains(x.Canonical))
                .ToDictionaryAsync(x => x.Canonical, StringComparer.Ordinal, cancellationToken);

            foreach (var canonical in canonicalTerms)
            {
                var analyzed = aggregate[canonical];
                var dictionaryEntry = toolkit.Dictionary?.Find(canonical);
                var reading = PreferReading(analyzed.Reading, dictionaryEntry?.Reading);
                var meaning = dictionaryEntry?.Meaning;

                if (!terms.TryGetValue(canonical, out var term))
                {
                    term = new Term
                    {
                        Language = language,
                        Canonical = canonical,
                        Reading = NullIfEmpty(reading),
                        Meaning = NullIfEmpty(meaning)
                    };

                    terms.Add(canonical, term);
                    db.Terms.Add(term);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(term.Reading) && !string.IsNullOrWhiteSpace(reading))
                {
                    term.Reading = reading;
                }

                if (string.IsNullOrWhiteSpace(term.Meaning) && !string.IsNullOrWhiteSpace(meaning))
                {
                    term.Meaning = meaning;
                }
            }

            await db.SaveChangesAsync(cancellationToken);

            episodeTerms.AddRange(aggregate.Select(item => new EpisodeTerm
            {
                EpisodeId = episodeId,
                TermId = terms[item.Key].Id,
                Occurrences = item.Value.Count,
                FirstCueStartMs = item.Value.FirstMs
            }));
        }

        if (episodeTerms.Count == 0)
        {
            return;
        }

        db.EpisodeTerms.AddRange(episodeTerms);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? PreferReading(string? primary, string? fallback) =>
        !string.IsNullOrWhiteSpace(primary) ? primary : fallback;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record TermAggregate(int Count, int FirstMs, string? Reading);
}
