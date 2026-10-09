using System.Security.Cryptography;
using System.Text;
using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Novels;

/// <summary>A chapter of an imported volume, in reading order.</summary>
public sealed record NovelVolumeChapterInput(
    string SourceKey,
    string Title,
    string Text,
    string? ContentJson = null,
    string? GroupTitle = null);

public sealed record NovelVolumeSyncResult(
    int Added,
    int Updated,
    int Removed,
    int Unchanged);

/// <summary>
/// The canonical write path for volume content. Books imports and EPUB light
/// novel volumes both persist their parsed chapters through
/// <see cref="SyncChaptersAsync"/>; web novels only need their implicit volume.
/// </summary>
public static class NovelVolumeContent
{
    private const int TemporaryNumberBase = 1_000_000;

    /// <summary>
    /// Returns the single implicit volume of a web novel or Books work,
    /// adding it to the context when the work is new.
    /// </summary>
    public static async Task<NovelVolume> EnsureImplicitVolumeAsync(
        AppDbContext db,
        NovelWork work,
        string kind,
        CancellationToken cancellationToken)
    {
        var volume = db.NovelVolumes.Local.FirstOrDefault(x => x.WorkId == work.Id)
            ?? await db.NovelVolumes
                .Where(x => x.WorkId == work.Id)
                .OrderBy(x => x.Number)
                .FirstOrDefaultAsync(cancellationToken);

        if (volume is not null)
        {
            return volume;
        }

        volume = new NovelVolume
        {
            WorkId = work.Id,
            Number = 1,
            Kind = kind,
            SourceKey = kind,
            ImportedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.NovelVolumes.Add(volume);
        return volume;
    }

    /// <summary>
    /// Replaces the chapters of one volume with <paramref name="chapters"/>.
    /// Existing chapters are matched by source key, then by identical text,
    /// then by a unique title, so their ids — and every progress row, bookmark,
    /// highlight and translation pointing at them — survive a re-import.
    /// Unchanged chapters are not touched. Afterwards the series is renumbered
    /// in volume order so chapter numbers stay the series reading order.
    /// </summary>
    public static async Task<NovelVolumeSyncResult> SyncChaptersAsync(
        AppDbContext db,
        NovelWork work,
        NovelVolume volume,
        IReadOnlyList<NovelVolumeChapterInput> chapters,
        CancellationToken cancellationToken)
    {
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var existing = db.Entry(volume).State == EntityState.Added
            ? []
            : await db.NovelChapters
                .Where(x => x.VolumeId == volume.Id)
                .ToListAsync(cancellationToken);

        var assignment = MatchChapters(existing, chapters);
        var now = DateTime.UtcNow;
        int added = 0, updated = 0, unchanged = 0;
        var ordered = new List<NovelChapter>(chapters.Count);

        for (var index = 0; index < chapters.Count; index++)
        {
            var input = chapters[index];
            var hash = Hash(input.Text);
            var title = Truncate(input.Title, 500);
            var sourceKey = Truncate(input.SourceKey, 2048);
            var groupTitle = input.GroupTitle is null ? null : Truncate(input.GroupTitle, 200);

            if (assignment[index] is not NovelChapter chapter)
            {
                chapter = new NovelChapter
                {
                    WorkId = work.Id,
                    VolumeId = volume.Id,
                    Number = -(TemporaryNumberBase + index + 1),
                    SourceUrl = sourceKey,
                    Title = title,
                    OriginalText = input.Text,
                    ContentJson = input.ContentJson,
                    GroupTitle = groupTitle,
                    SourceHash = hash,
                    ImportedAt = now,
                    UpdatedAt = now
                };
                db.NovelChapters.Add(chapter);
                added++;
            }
            else if (chapter.SourceHash != hash ||
                chapter.OriginalText != input.Text ||
                chapter.Title != title ||
                chapter.SourceUrl != sourceKey ||
                chapter.ContentJson != input.ContentJson ||
                chapter.GroupTitle != groupTitle)
            {
                chapter.SourceUrl = sourceKey;
                chapter.Title = title;
                chapter.OriginalText = input.Text;
                chapter.ContentJson = input.ContentJson;
                chapter.GroupTitle = groupTitle;
                chapter.SourceHash = hash;
                chapter.UpdatedAt = now;
                updated++;
            }
            else
            {
                unchanged++;
            }

            ordered.Add(chapter);
        }

        var matched = assignment.Where(x => x is not null).ToHashSet();
        var removed = 0;
        foreach (var stale in existing.Where(x => !matched.Contains(x)))
        {
            db.NovelChapters.Remove(stale);
            removed++;
        }

        await db.SaveChangesAsync(cancellationToken);

        await RenumberSeriesAsync(
            db,
            work.Id,
            volume.Id,
            ordered.Select(x => x.Id).ToArray(),
            cancellationToken);

        foreach (var entry in db.ChangeTracker.Entries<NovelChapter>().ToArray())
        {
            entry.State = EntityState.Detached;
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new NovelVolumeSyncResult(added, updated, removed, unchanged);
    }

    /// <summary>
    /// Renumbers every chapter of a work as volume order, then chapter order
    /// (the given order for the synced volume, the current order elsewhere).
    /// Numbers pass through unique negative values so the (WorkId, Number)
    /// unique index never sees a transient duplicate.
    /// </summary>
    public static async Task RenumberSeriesAsync(
        AppDbContext db,
        Guid workId,
        Guid? syncedVolumeId,
        IReadOnlyList<Guid> syncedOrder,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from chapter in db.NovelChapters.AsNoTracking()
            join volume in db.NovelVolumes.AsNoTracking()
                on chapter.VolumeId equals volume.Id
            where chapter.WorkId == workId
            select new
            {
                chapter.Id,
                chapter.Number,
                chapter.VolumeId,
                VolumeNumber = volume.Number
            })
            .ToListAsync(cancellationToken);

        var syncedPosition = syncedOrder
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);

        var desired = rows
            .OrderBy(x => x.VolumeNumber)
            .ThenBy(x => x.VolumeId == syncedVolumeId && syncedPosition.ContainsKey(x.Id)
                ? syncedPosition[x.Id]
                : x.Number)
            .Select((row, index) => (row.Id, row.Number, Target: index + 1))
            .Where(x => x.Number != x.Target)
            .ToArray();

        foreach (var (id, _, target) in desired)
        {
            await db.NovelChapters
                .Where(x => x.Id == id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(x => x.Number, -target),
                    cancellationToken);
        }

        foreach (var (id, _, target) in desired)
        {
            await db.NovelChapters
                .Where(x => x.Id == id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(x => x.Number, target),
                    cancellationToken);
        }
    }

    private static NovelChapter?[] MatchChapters(
        IReadOnlyList<NovelChapter> existing,
        IReadOnlyList<NovelVolumeChapterInput> inputs)
    {
        var assignment = new NovelChapter?[inputs.Count];
        var available = existing.ToList();

        void MatchBy(Func<NovelChapter, string> existingKey, Func<NovelVolumeChapterInput, string> inputKey)
        {
            var candidates = available
                .GroupBy(existingKey, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

            var inputCounts = inputs
                .Where((_, index) => assignment[index] is null)
                .GroupBy(inputKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            for (var index = 0; index < inputs.Count; index++)
            {
                if (assignment[index] is not null)
                {
                    continue;
                }

                var key = inputKey(inputs[index]);
                if (key.Length == 0 ||
                    inputCounts[key] != 1 ||
                    !candidates.Remove(key, out var chapter))
                {
                    continue;
                }

                assignment[index] = chapter;
                available.Remove(chapter);
            }
        }

        MatchBy(x => x.SourceUrl, x => Truncate(x.SourceKey, 2048));
        MatchBy(x => x.SourceHash, x => Hash(x.Text));
        MatchBy(x => x.Title, x => Truncate(x.Title, 500));

        // A replacement edition of the same chapter count keeps the place of every chapter nothing else identified, so reading progress, bookmarks and notes stay.
        if (existing.Count == inputs.Count && assignment.Any(chapter => chapter is null))
        {
            var ordered = existing.OrderBy(x => x.Number).ToArray();
            for (var index = 0; index < inputs.Count; index++)
            {
                if (assignment[index] is null && available.Contains(ordered[index]))
                {
                    assignment[index] = ordered[index];
                    available.Remove(ordered[index]);
                }
            }
        }

        return assignment;
    }

    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
