using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Reading;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.OfflineLibrary;

/// <summary>
/// Finds what the current profile is likely to consume next, from the
/// canonical progress owners only: <see cref="EpisodeProgressService"/>
/// (Continue Watching, <see cref="EpisodeSequence"/> for the following
/// episodes) and <c>NovelProgress</c> (one row per profile and work). It reads
/// and never writes, and keeps no state of its own.
/// <para>
/// Ordering: candidates are ranked by how far ahead they are (the episode or
/// chapter to continue with comes first for every series/work, then the one
/// after it, ...) and, within one depth, by most recent activity. A cap that
/// only fits a few items therefore keeps every active series/work ready one
/// step ahead instead of buffering one show entirely.
/// </para>
/// </summary>
public sealed class OfflinePrefetchCandidateSource(
    AppDbContext db,
    EpisodeProgressService episodeProgress,
    CurrentAccountContext currentAccount)
{
    /// <summary>Number of most recently active series/works considered per kind.</summary>
    public const int MaxContainers = 12;

    /// <summary>
    /// Chapter downloads carry text (original, structured blocks, translations); the
    /// character count is converted with the UTF-8 worst case for Japanese text. The
    /// client reports real sizes back in its inventory, so this only sizes the plan.
    /// </summary>
    public const int EstimatedBytesPerCharacter = 3;

    public async Task<IReadOnlyList<OfflinePrefetchCandidate>> GetAsync(
        OfflinePrefetchPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var ranked = new List<RankedCandidate>();

        if (policy.IncludeEpisodes)
        {
            ranked.AddRange(await EpisodeCandidatesAsync(policy.EpisodesAhead, cancellationToken));
        }

        if (policy.IncludeChapters)
        {
            ranked.AddRange(await ChapterCandidatesAsync(policy.ChaptersAhead, cancellationToken));
        }

        return
        [
            .. ranked
                .OrderBy(x => x.Depth)
                .ThenByDescending(x => x.Activity)
                .ThenBy(x => x.Candidate.ContainerId)
                .ThenBy(x => x.Candidate.ItemId)
                .Select(x => x.Candidate)
        ];
    }

    private async Task<IEnumerable<RankedCandidate>> EpisodeCandidatesAsync(
        int episodesAhead,
        CancellationToken cancellationToken)
    {
        var items = await episodeProgress.GetContinueWatchingAsync(MaxContainers, cancellationToken);
        if (items.Count == 0)
        {
            return [];
        }

        var animeIds = items.Select(x => x.AnimeId).Distinct().ToArray();
        var local = (await db.Episodes
            .AsNoTracking()
            .Where(x => animeIds.Contains(x.AnimeId))
            .Select(x => new
            {
                x.Id,
                x.AnimeId,
                x.SeasonNumber,
                x.Number,
                SizeBytes = db.MediaFiles
                    .Where(media => media.EpisodeId == x.Id)
                    .Max(media => (long?)media.SizeBytes)
            })
            .ToListAsync(cancellationToken))
            .Where(x => x.SizeBytes is > 0)
            .ToList();

        var watched = (await episodeProgress.GetForAnimesAsync(animeIds, cancellationToken))
            .Where(x => x.Value.IsCompleted)
            .Select(x => x.Key)
            .ToHashSet();

        var byId = local.ToDictionary(x => x.Id);
        var ranked = new List<RankedCandidate>();

        foreach (var item in items)
        {
            var keys = local
                .Where(x => x.AnimeId == item.AnimeId)
                .Select(x => new EpisodeOrderKey(x.Id, x.SeasonNumber, x.Number))
                .ToArray();

            var chain = new List<Guid>();
            Guid? cursor = item.EpisodeId;
            for (var steps = 0; cursor is { } current && chain.Count < episodesAhead && steps <= keys.Length; steps++)
            {
                if (byId.ContainsKey(current) && !watched.Contains(current))
                {
                    chain.Add(current);
                }

                cursor = EpisodeSequence.Resolve(keys, current).NextEpisodeId;
            }

            for (var depth = 0; depth < chain.Count; depth++)
            {
                var episode = byId[chain[depth]];
                ranked.Add(new RankedCandidate(
                    depth,
                    item.UpdatedAt,
                    new OfflinePrefetchCandidate(
                        OfflinePrefetchKind.Episode,
                        episode.Id,
                        item.AnimeId,
                        $"{item.AnimeTitle} S{episode.SeasonNumber:00}E{episode.Number:00}",
                        episode.SizeBytes!.Value)));
            }
        }

        return ranked;
    }

    private async Task<IEnumerable<RankedCandidate>> ChapterCandidatesAsync(
        int chaptersAhead,
        CancellationToken cancellationToken)
    {
        var positions = await (
            from progress in db.NovelProgress.AsNoTracking()
            join work in db.NovelWorks.AsNoTracking() on progress.WorkId equals work.Id
            join chapter in db.NovelChapters.AsNoTracking() on progress.ChapterId equals chapter.Id
            where progress.ProfileId == currentAccount.ProfileId
            orderby progress.UpdatedAt descending, progress.WorkId
            select new
            {
                progress.WorkId,
                Title = work.MetadataTitle ?? work.Title,
                chapter.Number,
                progress.PositionPermille,
                progress.UpdatedAt
            })
            .Take(MaxContainers)
            .ToListAsync(cancellationToken);

        var ranked = new List<RankedCandidate>();

        foreach (var position in positions)
        {
            // A chapter read to the end is done; continue with the one after it.
            var firstNumber = position.PositionPermille >= ContinueReadingQuery.FinishedPositionPermille
                ? position.Number + 1
                : position.Number;
            var workId = position.WorkId;

            var chapters = await db.NovelChapters
                .AsNoTracking()
                .Where(x => x.WorkId == workId && x.Number >= firstNumber && x.OriginalText != "")
                .OrderBy(x => x.Number)
                .Take(chaptersAhead)
                .Select(x => new
                {
                    x.Id,
                    x.Number,
                    x.Title,
                    Characters = x.OriginalText.Length
                        + (x.ContentJson == null ? 0 : x.ContentJson.Length)
                        + db.NovelTranslations
                            .Where(t => t.ChapterId == x.Id && t.SourceHash == x.SourceHash)
                            .Sum(t => t.Text.Length)
                })
                .ToListAsync(cancellationToken);

            for (var depth = 0; depth < chapters.Count; depth++)
            {
                var chapter = chapters[depth];
                ranked.Add(new RankedCandidate(
                    depth,
                    position.UpdatedAt,
                    new OfflinePrefetchCandidate(
                        OfflinePrefetchKind.Chapter,
                        chapter.Id,
                        workId,
                        string.IsNullOrWhiteSpace(chapter.Title)
                            ? $"{position.Title} {chapter.Number}"
                            : $"{position.Title} · {chapter.Title}",
                        (long)chapter.Characters * EstimatedBytesPerCharacter)));
            }
        }

        return ranked;
    }

    private sealed record RankedCandidate(int Depth, DateTime Activity, OfflinePrefetchCandidate Candidate);
}
