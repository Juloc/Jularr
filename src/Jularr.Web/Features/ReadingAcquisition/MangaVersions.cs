using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.ReadingAcquisition;

public static class MangaFileQuality
{
    // The quality key of a library file as the profile's quality order names it: its archive format, or unknown for a folder of images.
    public static string Of(string? sourcePath, string? sourceKind) =>
        ReadingReleaseEvidenceParser.QualityOf(
            sourceKind == "directory"
                ? ReadingReleaseFormat.Unknown
                : Path.GetExtension(sourcePath ?? "").ToLowerInvariant() switch
                {
                    ".cbz" => ReadingReleaseFormat.Cbz,
                    ".zip" => ReadingReleaseFormat.Zip,
                    ".cbr" => ReadingReleaseFormat.Cbr,
                    _ => ReadingReleaseFormat.Unknown
                });
}

// Several library files that hold exactly the same volumes or chapters are versions of them (a file nothing is tied to is known by the volume or chapters its name states). The reader shows the best one by the Work's profile (the others carry
// SupersededById), nothing is deleted, and reading progress and bookmarks follow whichever version is shown.
public sealed class MangaVersionSelector(AppDbContext db, QualityProfileStore profiles)
{
    private sealed class VersionRow
    {
        public string LocalId { get; set; } = "";

        public Guid? WorkVolumeId { get; set; }

        public Guid? WorkChapterId { get; set; }

        public string SourcePath { get; set; } = "";

        public string SourceKind { get; set; } = "";

        public int PageCount { get; set; }

        public string SourceUpdatedAt { get; set; } = "";

        public string? SupersededById { get; set; }
    }

    public async Task ReselectAsync(long workId, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQuery<VersionRow>(
            $"""
            SELECT local."Id" AS "LocalId", bound."WorkVolumeId", bound."WorkChapterId", local."SourcePath", local."SourceKind", local."PageCount", local."SourceUpdatedAt", local."SupersededById"
            FROM "MangaChapters" local
            JOIN "WorkSourceLinks" link ON link."SourceId"::text = local."SeriesId" AND link."SourceKind" = 3
            LEFT JOIN "WorkUnitBindings" bound ON bound."LocalKind" = 1 AND bound."LocalId" = local."Id"
            WHERE link."WorkId" = {workId}
            """).ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        var profile = await profiles.ResolveAsync(MediaAcquisitionKind.Manga, workId, cancellationToken);
        var files = rows
            .GroupBy(row => row.LocalId)
            .Select(group => (
                Id: group.Key,
                Units: UnitsOf(group.ToArray()),
                First: group.First()))
            .ToArray();

        var shown = new Dictionary<string, string?>();
        foreach (var versions in files.GroupBy(file => file.Units))
        {
            var best = versions
                .OrderBy(file => Eligible(profile, file.First) ? 0 : 1)
                .ThenBy(file => UpgradePolicy.RankOf(profile, MangaFileQuality.Of(file.First.SourcePath, file.First.SourceKind)))
                .ThenByDescending(file => file.First.PageCount)
                .ThenByDescending(file => file.First.SourceUpdatedAt, StringComparer.Ordinal)
                .ThenBy(file => file.Id, StringComparer.Ordinal)
                .First().Id;
            foreach (var file in versions)
            {
                shown[file.Id] = file.Id == best ? null : best;
            }
        }

        var changed = files.Where(file => shown[file.Id] != file.First.SupersededById).ToArray();
        if (changed.Length == 0)
        {
            return;
        }

        await db.Database.InTransactionAsync(
            async () =>
            {
                // The shown versions first, so a version that is shown again is never left pointing at one that was shown before it.
                foreach (var file in changed.OrderBy(file => shown[file.Id] is null ? 0 : 1))
                {
                    var replacement = shown[file.Id];
                    await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "MangaChapters" SET "SupersededById" = {replacement} WHERE "Id" = {file.Id}""", cancellationToken);
                    if (replacement is not null)
                    {
                        await MoveReadingStateAsync(file.Id, replacement, cancellationToken);
                    }
                }
            },
            cancellationToken);
    }

    private static string UnitsOf(VersionRow[] rows)
    {
        if (rows.Any(row => row.WorkVolumeId is not null || row.WorkChapterId is not null))
        {
            return string.Join(',', rows.Select(row => row.WorkVolumeId ?? row.WorkChapterId).OrderBy(id => id));
        }

        var named = ReadingReleaseParser.Parse(Path.GetFileNameWithoutExtension(rows[0].SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        return named.VolumeNumber is null && named.ChapterStart is null
            ? rows[0].LocalId
            : $"name:{named.VolumeNumber}-{named.VolumeEnd}|{named.ChapterStart}-{named.ChapterEnd}";
    }

    // A version the profile does not allow is shown only while no allowed version exists.
    private static bool Eligible(QualityProfile profile, VersionRow row) =>
        profile.AllowedQualities.Contains(MangaFileQuality.Of(row.SourcePath, row.SourceKind), StringComparer.OrdinalIgnoreCase);

    private async Task MoveReadingStateAsync(string from, string to, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "MangaProgress" SET "ChapterId" = {to}, "PageIndex" = GREATEST(0, LEAST("PageIndex", (SELECT "PageCount" FROM "MangaChapters" WHERE "Id" = {to}) - 1))
            WHERE "ChapterId" = {from}
            """,
            cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "MangaBookmarks" SET "ChapterId" = {to}, "PageIndex" = GREATEST(0, LEAST("PageIndex", (SELECT "PageCount" FROM "MangaChapters" WHERE "Id" = {to}) - 1))
            WHERE "ChapterId" = {from}
            """,
            cancellationToken);
    }
}

// A Manga unit is upgradable while the best version it holds is below what its profile wants (a ZIP where the profile wants a CBZ). One assessor judges one kind
// of queue row: the volumes, the chapters, or the Work itself while it has no structure.
public sealed class MangaUpgradeAssessor(WantedTargetKind targetKind, ReadingCoverageService coverage, QualityProfileStore profiles) : IUpgradeAssessor
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Manga;

    public WantedTargetKind TargetKind => targetKind;

    public async Task<IReadOnlyList<HeldTarget>> UpgradableAsync(long workId, IReadOnlyList<HeldTarget> held, CancellationToken cancellationToken)
    {
        var view = await coverage.LoadAsync(workId, cancellationToken);
        if (targetKind == WantedTargetKind.Work)
        {
            var profile = await profiles.ResolveAsync(MediaAcquisitionKind.Manga, workId, cancellationToken);
            return !view.HasStructure && UpgradePolicy.Assess(profile, view.InstalledQuality).IsUpgradable ? held : [];
        }

        var upgradable = targetKind == WantedTargetKind.Volume
            ? view.Volumes.Where(unit => unit.UpgradeWanted).Select(unit => unit.Id).ToHashSet()
            : view.Volumes.SelectMany(unit => unit.Chapters).Concat(view.LooseChapters).Where(unit => unit.UpgradeWanted).Select(unit => unit.Id).ToHashSet();
        return [.. held.Where(target => target.TargetId is { } id && upgradable.Contains(id))];
    }
}
