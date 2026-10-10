using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.ReadingAcquisition;

public enum ReadingCoverageState
{
    Missing,
    Partial,
    Installed
}

public sealed record ReadingChapterUnit(Guid Id, double Number, string? Title, bool IsSpecial, Guid? VolumeId, bool Monitored, bool Installed, bool Wanted, bool? Decision = null, string? InstalledQuality = null, bool UpgradeWanted = false);

public sealed record ReadingVolumeUnit(Guid Id, int Number, string? Title, bool Monitored, ReadingCoverageState State, bool Wanted, IReadOnlyList<ReadingChapterUnit> Chapters, bool? Decision = null, string? InstalledQuality = null, bool UpgradeWanted = false)
{
    public int InstalledChapters => Chapters.Count(chapter => chapter.Installed);
}

public sealed record ReadingCoverageView(
    long WorkId,
    bool Monitored,
    IReadOnlyList<ReadingVolumeUnit> Volumes,
    IReadOnlyList<ReadingChapterUnit> LooseChapters,
    int LocalChapters,
    int UntiedLocalChapters,
    ReadingWant Want,
    string? InstalledQuality = null)
{
    public bool HasStructure => Volumes.Count > 0 || LooseChapters.Count > 0;
}

// The one calculation of what a Manga Work holds and lacks, for the Admin page and release judging; WantedSql applies the same rules. A volume is installed when a
// file is tied to it or all its chapters are, a chapter when a file is tied to it or its volume is; a chapter inside a monitored volume is covered by the volume.
// The installed quality of a unit is the best of the versions tied to it (of a volume held through its chapters, the worst chapter), and an installed unit the
// profile still wants better is wanted for an upgrade by the same rules as a missing one.
public sealed class ReadingCoverageService(AppDbContext db, MonitoringResolver monitoring, QualityProfileStore? profiles = null)
{
    private sealed class TiedUnit
    {
        public Guid? WorkVolumeId { get; set; }

        public Guid? WorkChapterId { get; set; }

        public string SourcePath { get; set; } = "";

        public string SourceKind { get; set; } = "";
    }

    private sealed class LocalFile
    {
        public string SourcePath { get; set; } = "";

        public string SourceKind { get; set; } = "";
    }

    public async Task<ReadingCoverageView> LoadAsync(long workId, CancellationToken cancellationToken, bool wholeTitleAsked = false)
    {
        var volumes = await db.WorkVolumes.AsNoTracking().Where(volume => volume.WorkId == workId && volume.ExternalId != null).OrderBy(volume => volume.Number).ToListAsync(cancellationToken);
        var chapters = await db.WorkChapters.AsNoTracking().Where(chapter => chapter.WorkId == workId && chapter.ExternalId != null).OrderBy(chapter => chapter.Number).ToListAsync(cancellationToken);
        var tied = await db.Database.SqlQuery<TiedUnit>(
            $"""
            SELECT bound."WorkVolumeId", bound."WorkChapterId", local."SourcePath", local."SourceKind" FROM "WorkUnitBindings" bound
            JOIN "MangaChapters" local ON local."Id" = bound."LocalId"
            WHERE bound."WorkId" = {workId} AND bound."LocalKind" = 1
            """).ToListAsync(cancellationToken);
        var locals = await db.Database.SqlQuery<LocalFile>(
            $"""
            SELECT chapter."SourcePath", chapter."SourceKind" FROM "MangaChapters" chapter
            JOIN "WorkSourceLinks" link ON link."SourceId"::text = chapter."SeriesId" AND link."SourceKind" = 3
            WHERE link."WorkId" = {workId}
            """).ToListAsync(cancellationToken);
        var untied = await db.Database.SqlQuery<int>(
            $"""
            SELECT COUNT(*)::int AS "Value" FROM "MangaChapters" chapter
            JOIN "WorkSourceLinks" link ON link."SourceId"::text = chapter."SeriesId" AND link."SourceKind" = 3
            WHERE link."WorkId" = {workId} AND NOT EXISTS (SELECT 1 FROM "WorkUnitBindings" bound WHERE bound."LocalKind" = 1 AND bound."LocalId" = chapter."Id")
            """).SingleAsync(cancellationToken);
        var decisions = await monitoring.LoadAsync(workId, cancellationToken);
        var profile = profiles is null ? null : await profiles.ResolveAsync(MediaAcquisitionKind.Manga, workId, cancellationToken);

        var volumeTied = tied.Where(item => item.WorkVolumeId is not null).Select(item => item.WorkVolumeId!.Value).ToHashSet();
        var chapterTied = tied.Where(item => item.WorkChapterId is not null).Select(item => item.WorkChapterId!.Value).ToHashSet();
        var volumeQuality = tied.Where(item => item.WorkVolumeId is not null).GroupBy(item => item.WorkVolumeId!.Value).ToDictionary(group => group.Key, group => Best(profile, group));
        var chapterQuality = tied.Where(item => item.WorkChapterId is not null).GroupBy(item => item.WorkChapterId!.Value).ToDictionary(group => group.Key, group => Best(profile, group));
        var identifiedVolumes = volumes.Select(volume => volume.Id).ToHashSet();
        var monitoredVolumes = volumes.Where(volume => decisions.IsMonitored(volume.Id)).Select(volume => volume.Id).ToHashSet();

        bool Upgradable(string? quality) => profile is not null && UpgradePolicy.Assess(profile, quality).IsUpgradable;

        var chapterUnits = chapters.Select(chapter =>
        {
            var monitored = decisions.IsMonitored(chapter.Id, chapter.VolumeId);
            var inVolume = chapter.VolumeId is { } owner && identifiedVolumes.Contains(owner);
            var installed = chapterTied.Contains(chapter.Id) || (chapter.VolumeId is { } holder && volumeTied.Contains(holder));
            var candidate = (monitored && !(chapter.VolumeId is { } parent && monitoredVolumes.Contains(parent)))
                || (wholeTitleAsked && !inVolume && decisions.DecisionOf(chapter.Id) != false);
            var quality = chapterQuality.TryGetValue(chapter.Id, out var own) ? own : chapter.VolumeId is { } via && volumeQuality.TryGetValue(via, out var inherited) ? inherited : null;
            return new ReadingChapterUnit(chapter.Id, chapter.Number, chapter.Title, chapter.IsSpecial, chapter.VolumeId, monitored, installed, !installed && candidate, decisions.DecisionOf(chapter.Id), quality, installed && candidate && Upgradable(quality));
        }).ToArray();

        var volumeUnits = volumes
            .Select(volume =>
            {
                var parts = chapterUnits.Where(chapter => chapter.VolumeId == volume.Id).OrderBy(chapter => chapter.Number).ToArray();
                var state = volumeTied.Contains(volume.Id) || (parts.Length > 0 && parts.All(chapter => chapter.Installed))
                    ? ReadingCoverageState.Installed
                    : parts.Any(chapter => chapter.Installed) ? ReadingCoverageState.Partial : ReadingCoverageState.Missing;
                var monitored = decisions.IsMonitored(volume.Id);
                var candidate = monitored || (wholeTitleAsked && decisions.DecisionOf(volume.Id) != false);
                var quality = volumeQuality.TryGetValue(volume.Id, out var own) ? own : state == ReadingCoverageState.Installed ? Worst(profile, parts.Select(part => part.InstalledQuality)) : null;
                return new ReadingVolumeUnit(volume.Id, volume.Number, volume.Title, monitored, state, state != ReadingCoverageState.Installed && candidate, parts, decisions.DecisionOf(volume.Id), quality, state == ReadingCoverageState.Installed && candidate && Upgradable(quality));
            })
            .ToArray();
        var loose = chapterUnits.Where(chapter => chapter.VolumeId is not { } owner || !identifiedVolumes.Contains(owner)).OrderBy(chapter => chapter.Number).ToArray();

        var want = new ReadingWant(
            [.. volumeUnits.Where(volume => volume.Wanted).Select(volume => volume.Number)],
            [.. chapterUnits.Where(chapter => chapter.Wanted).Select(chapter => chapter.Number).Order()],
            [.. volumeUnits.Where(volume => volume.State == ReadingCoverageState.Installed).Select(volume => volume.Number)],
            [.. chapterUnits.Where(chapter => chapter.Installed).Select(chapter => chapter.Number).Order()])
        {
            UpgradeVolumes = volumeUnits.Where(volume => volume.UpgradeWanted).ToDictionary(volume => volume.Number, volume => volume.InstalledQuality),
            UpgradeChapters = chapterUnits.Where(chapter => chapter.UpgradeWanted).ToDictionary(chapter => chapter.Number, chapter => chapter.InstalledQuality),
            Profile = profile
        };
        return new ReadingCoverageView(workId, decisions.IsWorkMonitored, volumeUnits, loose, locals.Count, untied, want, Best(profile, locals.Select(file => MangaFileQuality.Of(file.SourcePath, file.SourceKind))));
    }

    public async Task<string?> InstalledQualityAsync(long workId, CancellationToken cancellationToken)
    {
        var profile = profiles is null ? null : await profiles.ResolveAsync(MediaAcquisitionKind.Manga, workId, cancellationToken);
        var locals = await db.Database.SqlQuery<LocalFile>(
            $"""
            SELECT chapter."SourcePath", chapter."SourceKind" FROM "MangaChapters" chapter
            JOIN "WorkSourceLinks" link ON link."SourceId"::text = chapter."SeriesId" AND link."SourceKind" = 3
            WHERE link."WorkId" = {workId}
            """).ToListAsync(cancellationToken);
        return Best(profile, locals.Select(file => MangaFileQuality.Of(file.SourcePath, file.SourceKind)));
    }

    public async Task<bool> WholeTitleAskedAsync(Guid requestId, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<bool>(
            $"""SELECT EXISTS (SELECT 1 FROM "RequestTargets" target WHERE target."RequestId" = {requestId.ToString()} AND target."TargetKind" = 0) AS "Value" """).SingleAsync(cancellationToken);

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

        // A named unit is searched whether or not Monitoring wants it; an installed one only while the profile wants a better version of it.
        var named = view.Want with
        {
            Volumes = [.. view.Volumes.Where(unit => unit.Number == volume && unit.State != ReadingCoverageState.Installed).Select(unit => unit.Number)],
            Chapters = [.. view.Volumes.SelectMany(unit => unit.Chapters).Concat(view.LooseChapters)
                .Where(unit => !unit.Installed && chapterStart is { } first && unit.Number >= first && unit.Number <= (chapterEnd ?? first))
                .Select(unit => unit.Number).Order()]
        } with
        {
            UpgradeVolumes = view.Want.UpgradeVolumes.Where(unit => unit.Key == volume).ToDictionary(unit => unit.Key, unit => unit.Value),
            UpgradeChapters = view.Want.UpgradeChapters.Where(unit => chapterStart is { } first && unit.Key >= first && unit.Key <= (chapterEnd ?? first)).ToDictionary(unit => unit.Key, unit => unit.Value)
        };
        return named.IsEmpty ? null : named;
    }

    private static string? Best(QualityProfile? profile, IEnumerable<TiedUnit> versions) =>
        Best(profile, versions.Select(version => MangaFileQuality.Of(version.SourcePath, version.SourceKind)));

    private static string? Best(QualityProfile? profile, IEnumerable<string?> qualities) => profile is null ? null : UpgradePolicy.Best(profile, qualities);

    // A volume held only through its chapters is as good as its worst chapter; one chapter of unknown quality makes it unknown.
    private static string? Worst(QualityProfile? profile, IEnumerable<string?> qualities)
    {
        if (profile is null)
        {
            return null;
        }

        var known = qualities.ToArray();
        return known.Length == 0 || known.Any(quality => UpgradePolicy.RankOf(profile, quality) == int.MaxValue)
            ? null
            : known.OrderByDescending(quality => UpgradePolicy.RankOf(profile, quality)).First();
    }
}
