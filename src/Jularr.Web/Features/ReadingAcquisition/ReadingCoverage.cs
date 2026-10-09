using Jularr.Web.Data;
using Jularr.Web.Features.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.ReadingAcquisition;

public enum ReadingCoverageState
{
    Missing,
    Partial,
    Installed
}

/// <param name="Wanted">Whether Monitoring or a request for the whole title wants it now, which is what the Wanted queue lists.</param>
/// <param name="Decision">The owner's own decision on the unit; null while it follows its volume or the Work.</param>
public sealed record ReadingChapterUnit(Guid Id, double Number, string? Title, bool IsSpecial, Guid? VolumeId, bool Monitored, bool Installed, bool Wanted, bool? Decision = null);

public sealed record ReadingVolumeUnit(Guid Id, int Number, string? Title, bool Monitored, ReadingCoverageState State, bool Wanted, IReadOnlyList<ReadingChapterUnit> Chapters, bool? Decision = null)
{
    public int InstalledChapters => Chapters.Count(chapter => chapter.Installed);
}

/// <param name="LocalChapters">The chapters of the Work's series in the library, however they are tied.</param>
/// <param name="UntiedLocalChapters">Library chapters nothing ties to a volume or chapter yet; while any exist the Work is not wanted unit by unit.</param>
public sealed record ReadingCoverageView(
    long WorkId,
    bool Monitored,
    IReadOnlyList<ReadingVolumeUnit> Volumes,
    IReadOnlyList<ReadingChapterUnit> LooseChapters,
    int LocalChapters,
    int UntiedLocalChapters,
    ReadingWant Want)
{
    public bool HasStructure => Volumes.Count > 0 || LooseChapters.Count > 0;
}

/// <summary>
/// The one calculation of what a Manga Work holds and lacks, read by the Admin page and by release judging. A volume is installed when a library file is
/// tied to it or when every chapter it consists of is installed; a chapter is installed when a library file is tied to it or its volume is. Only
/// provider-identified units count, so a Work whose structure is unknown is never wanted unit by unit. A chapter inside a monitored volume is covered by
/// the volume, and a request for the whole title wants every missing unit except one the owner switched off. The Wanted SQL (<c>WantedSql</c>) applies the
/// same rules.
/// </summary>
public sealed class ReadingCoverageService(AppDbContext db, MonitoringResolver monitoring)
{
    private sealed class TiedUnit
    {
        public Guid? WorkVolumeId { get; set; }

        public Guid? WorkChapterId { get; set; }
    }

    public async Task<ReadingCoverageView> LoadAsync(long workId, CancellationToken cancellationToken, bool wholeTitleAsked = false)
    {
        var volumes = await db.WorkVolumes.AsNoTracking().Where(volume => volume.WorkId == workId && volume.ExternalId != null).OrderBy(volume => volume.Number).ToListAsync(cancellationToken);
        var chapters = await db.WorkChapters.AsNoTracking().Where(chapter => chapter.WorkId == workId && chapter.ExternalId != null).OrderBy(chapter => chapter.Number).ToListAsync(cancellationToken);
        var tied = await db.Database.SqlQuery<TiedUnit>(
            $"""
            SELECT DISTINCT bound."WorkVolumeId", bound."WorkChapterId" FROM "WorkUnitBindings" bound
            JOIN "MangaChapters" local ON local."Id" = bound."LocalId"
            WHERE bound."WorkId" = {workId} AND bound."LocalKind" = 1
            """).ToListAsync(cancellationToken);
        var local = await db.Database.SqlQuery<int>(
            $"""
            SELECT COUNT(*)::int AS "Value" FROM "MangaChapters" chapter
            JOIN "WorkSourceLinks" link ON link."SourceId"::text = chapter."SeriesId" AND link."SourceKind" = 3
            WHERE link."WorkId" = {workId}
            """).SingleAsync(cancellationToken);
        var untied = await db.Database.SqlQuery<int>(
            $"""
            SELECT COUNT(*)::int AS "Value" FROM "MangaChapters" chapter
            JOIN "WorkSourceLinks" link ON link."SourceId"::text = chapter."SeriesId" AND link."SourceKind" = 3
            WHERE link."WorkId" = {workId} AND NOT EXISTS (SELECT 1 FROM "WorkUnitBindings" bound WHERE bound."LocalKind" = 1 AND bound."LocalId" = chapter."Id")
            """).SingleAsync(cancellationToken);
        var decisions = await monitoring.LoadAsync(workId, cancellationToken);

        var volumeTied = tied.Where(item => item.WorkVolumeId is not null).Select(item => item.WorkVolumeId!.Value).ToHashSet();
        var chapterTied = tied.Where(item => item.WorkChapterId is not null).Select(item => item.WorkChapterId!.Value).ToHashSet();
        var identifiedVolumes = volumes.Select(volume => volume.Id).ToHashSet();
        var monitoredVolumes = volumes.Where(volume => decisions.IsMonitored(volume.Id)).Select(volume => volume.Id).ToHashSet();

        var chapterUnits = chapters.Select(chapter =>
        {
            var monitored = decisions.IsMonitored(chapter.Id, chapter.VolumeId);
            var inVolume = chapter.VolumeId is { } owner && identifiedVolumes.Contains(owner);
            var installed = chapterTied.Contains(chapter.Id) || (chapter.VolumeId is { } holder && volumeTied.Contains(holder));
            var wanted = !installed
                && ((monitored && !(chapter.VolumeId is { } parent && monitoredVolumes.Contains(parent)))
                    || (wholeTitleAsked && !inVolume && decisions.DecisionOf(chapter.Id) != false));
            return new ReadingChapterUnit(chapter.Id, chapter.Number, chapter.Title, chapter.IsSpecial, chapter.VolumeId, monitored, installed, wanted, decisions.DecisionOf(chapter.Id));
        }).ToArray();

        var volumeUnits = volumes
            .Select(volume =>
            {
                var parts = chapterUnits.Where(chapter => chapter.VolumeId == volume.Id).OrderBy(chapter => chapter.Number).ToArray();
                var state = volumeTied.Contains(volume.Id) || (parts.Length > 0 && parts.All(chapter => chapter.Installed))
                    ? ReadingCoverageState.Installed
                    : parts.Any(chapter => chapter.Installed) ? ReadingCoverageState.Partial : ReadingCoverageState.Missing;
                var monitored = decisions.IsMonitored(volume.Id);
                var wanted = state != ReadingCoverageState.Installed && (monitored || (wholeTitleAsked && decisions.DecisionOf(volume.Id) != false));
                return new ReadingVolumeUnit(volume.Id, volume.Number, volume.Title, monitored, state, wanted, parts, decisions.DecisionOf(volume.Id));
            })
            .ToArray();
        var loose = chapterUnits.Where(chapter => chapter.VolumeId is not { } owner || !identifiedVolumes.Contains(owner)).OrderBy(chapter => chapter.Number).ToArray();

        var want = new ReadingWant(
            [.. volumeUnits.Where(volume => volume.Wanted).Select(volume => volume.Number)],
            [.. chapterUnits.Where(chapter => chapter.Wanted).Select(chapter => chapter.Number).Order()],
            [.. volumeUnits.Where(volume => volume.State == ReadingCoverageState.Installed).Select(volume => volume.Number)],
            [.. chapterUnits.Where(chapter => chapter.Installed).Select(chapter => chapter.Number).Order()]);
        return new ReadingCoverageView(workId, decisions.IsWorkMonitored, volumeUnits, loose, local, untied, want);
    }

    /// <summary>Whether the request names the whole title (Search now, an owner's request), which wants every missing unit that is not switched off.</summary>
    public async Task<bool> WholeTitleAskedAsync(Guid requestId, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<bool>(
            $"""SELECT EXISTS (SELECT 1 FROM "RequestTargets" target WHERE target."RequestId" = {requestId.ToString()} AND target."TargetKind" = 0) AS "Value" """).SingleAsync(cancellationToken);

    /// <summary>
    /// What a release is judged against for one request: the wanted units of the Work, or for a request that names a volume or chapters (Search now on one row)
    /// exactly the missing units it names, whether or not Monitoring wants them. Null while the Work has no provider-identified structure (or a library chapter
    /// nothing is tied to yet), or when the named unit is not part of it; an empty result means nothing is missing.
    /// </summary>
    public async Task<ReadingWant?> WantAsync(long workId, bool wholeTitleAsked, int? volume, double? chapterStart, double? chapterEnd, CancellationToken cancellationToken)
    {
        var view = await LoadAsync(workId, cancellationToken, wholeTitleAsked);
        if (!view.HasStructure || view.UntiedLocalChapters > 0)
        {
            return null;
        }

        if (volume is null && chapterStart is null)
        {
            return view.Want;
        }

        var narrowed = view.Want with
        {
            Volumes = [.. view.Volumes.Where(unit => unit.Number == volume && unit.State != ReadingCoverageState.Installed).Select(unit => unit.Number)],
            Chapters = [.. view.Volumes.SelectMany(unit => unit.Chapters).Concat(view.LooseChapters)
                .Where(unit => !unit.Installed && chapterStart is { } first && unit.Number >= first && unit.Number <= (chapterEnd ?? first))
                .Select(unit => unit.Number).Order()]
        };
        return narrowed.IsEmpty ? null : narrowed;
    }
}
