using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Operations;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.ReadingAcquisition;

public sealed record ReadingIdentityAdmin(string Provider, string ExternalId, bool IsPrimary);

/// <param name="Count">The pages of a Manga volume or chapter, or the chapters of a Light Novel volume.</param>
public sealed record ReadingLocalFile(Guid Id, double Number, int? VolumeNumber, string Name, int Count, string Format, string SourcePath, bool Superseded = false, string? ReadUrl = null, string? Quality = null);

/// <summary>One source of a Light Novel Work: a published EPUB library or a public web novel, kept apart because they are different editions.</summary>
public sealed record ReadingSourceAdmin(string Provider, string Label, string? Url, bool IsWeb);

// What the running download of the open request holds: the release it was sent for and how far the download is.
public sealed record ReadingTransfer(AcquisitionRequestStatus Status, string Release, ReadingReleaseInfo Parsed, int? ProgressPercent);

public sealed record ReadingWorkAdminView(
    long WorkId,
    string Title,
    string? NativeTitle,
    IReadOnlyList<string> Aliases,
    string? Description,
    string? CoverUrl,
    string? Status,
    int? Year,
    IReadOnlyList<ReadingIdentityAdmin> Identities,
    string? LibraryUrl,
    string? ReaderUrl,
    ReadingCoverageView Coverage,
    AcquisitionRequest? Request,
    IReadOnlyList<AcquisitionRequest> History,
    string ProfileId,
    string ProfileName,
    string? AssignedProfileId,
    IReadOnlyList<(string Id, string Name)> Profiles,
    IReadOnlyList<ReadingLocalFile> Files,
    ReadingTransfer? Transfer = null,
    MediaAcquisitionKind Kind = MediaAcquisitionKind.Manga,
    string? Author = null,
    IReadOnlyList<ReadingSourceAdmin>? Sources = null)
{
    public bool IsNovel => Kind == MediaAcquisitionKind.LightNovel;

    public bool IsTransferring(ReadingVolumeUnit unit) => Transfer is { } transfer && transfer.Parsed.HoldsVolume(unit.Number) && (unit.State != ReadingCoverageState.Installed || unit.UpgradeWanted);

    public bool IsTransferring(ReadingChapterUnit unit) => Transfer is { } transfer && transfer.Parsed.HoldsChapter(unit.Number) && (!unit.Installed || unit.UpgradeWanted);

    public bool RequestIsOpen => Request is { Status: not (AcquisitionRequestStatus.Completed or AcquisitionRequestStatus.Rejected or AcquisitionRequestStatus.Failed) };

    public bool RequestIsWaiting => Request is { Status: AcquisitionRequestStatus.Approved };

    public bool RequestFailed => Request is { Status: AcquisitionRequestStatus.Failed };

    public IReadOnlyList<string> Formats => [.. Files.Select(file => file.Format).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    public (int Installed, int Total) VolumeCount => (Coverage.Volumes.Count(volume => volume.State == ReadingCoverageState.Installed), Coverage.Volumes.Count);

    public (int Installed, int Total) ChapterCount
    {
        get
        {
            var all = Coverage.Volumes.SelectMany(volume => volume.Chapters).Concat(Coverage.LooseChapters).ToArray();
            return (all.Count(chapter => chapter.Installed), all.Length);
        }
    }

    public bool CanSearch => (!RequestIsOpen || RequestIsWaiting)
        && (Coverage.HasStructure
            ? Coverage.Volumes.Any(volume => volume.State != ReadingCoverageState.Installed || volume.UpgradeWanted) || Coverage.LooseChapters.Any(chapter => !chapter.Installed || chapter.UpgradeWanted)
            : Coverage.LocalChapters == 0);
}

public sealed class ReadingWorkAdminQuery(AppDbContext db, ReadingCoverageService coverage, AcquisitionAccessStore requests, QualityProfileStore? profiles = null, OperationStore? operations = null)
{
    public async Task<ReadingWorkAdminView?> GetAsync(long workId, CancellationToken cancellationToken)
    {
        var work = await db.Works.AsNoTracking()
            .Where(item => item.Id == workId && (item.MediaType == WorkMediaType.Manga || item.MediaType == WorkMediaType.LightNovel))
            .Select(item => new { item.CanonicalTitle, item.Year, item.MediaType })
            .SingleOrDefaultAsync(cancellationToken);
        if (work is null)
        {
            return null;
        }

        var kind = work.MediaType == WorkMediaType.LightNovel ? MediaAcquisitionKind.LightNovel : MediaAcquisitionKind.Manga;
        var identities = await db.WorkExternalIdentities.AsNoTracking()
            .Where(item => item.WorkId == workId && item.MediaType == work.MediaType && (item.Provider == NovelAniListProvider.ProviderKey || (kind == MediaAcquisitionKind.LightNovel && item.Provider == NcodeNovelSourceProvider.ProviderKey)))
            .OrderByDescending(item => item.Provider == NovelAniListProvider.ProviderKey)
            .ThenByDescending(item => item.IsPrimary)
            .Select(item => new ReadingIdentityAdmin(item.Provider, item.ExternalId, item.IsPrimary))
            .ToListAsync(cancellationToken);
        var names = await db.WorkTitles.AsNoTracking().Where(item => item.WorkId == workId).OrderBy(item => item.CreatedAt).Select(item => new { item.Value, item.TitleType }).ToListAsync(cancellationToken);

        var local = kind == MediaAcquisitionKind.LightNovel ? await NovelLocalAsync(workId, cancellationToken) : await MangaLocalAsync(workId, cancellationToken);
        var identity = identities.FirstOrDefault();
        var request = identity is null ? null : await requests.FindLatestAsync(kind, identity.Provider, identity.ExternalId, cancellationToken);
        var asked = request is { Status: not (AcquisitionRequestStatus.Completed or AcquisitionRequestStatus.Rejected or AcquisitionRequestStatus.Failed) } && await coverage.WholeTitleAskedAsync(request.Id, cancellationToken);
        var view = await coverage.LoadAsync(workId, cancellationToken, asked);
        var historyIds = await db.Database.SqlQuery<string>($"""SELECT request."Id" AS "Value" FROM "AcquisitionRequests" request WHERE request."WorkId" = {workId} ORDER BY request."CreatedAt" DESC LIMIT 10""").ToListAsync(cancellationToken);
        var history = new List<AcquisitionRequest>();
        foreach (var historyId in historyIds)
        {
            if (Guid.TryParse(historyId, out var requestId) && await requests.GetAsync(requestId, cancellationToken) is { } entry)
            {
                history.Add(entry);
            }
        }

        var profile = profiles is null ? null : await profiles.ResolveAsync(kind, workId, cancellationToken);
        var state = profiles is null ? null : await profiles.LoadAsync(cancellationToken);
        var title = local.Title ?? work.CanonicalTitle;
        return new ReadingWorkAdminView(
            workId,
            title,
            local.NativeTitle ?? names.FirstOrDefault(name => name.TitleType == WorkTitleType.Native)?.Value,
            [.. names.Select(name => name.Value).Where(value => !value.Equals(title, StringComparison.OrdinalIgnoreCase) && !value.Equals(local.NativeTitle, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)],
            local.Description,
            local.CoverUrl,
            local.Status,
            work.Year,
            identities,
            local.LibraryUrl,
            local.ReaderUrl,
            view,
            request,
            history,
            profile?.Id ?? "",
            profile?.Name ?? "",
            state?.WorkAssignments.GetValueOrDefault(workId.ToString("D")),
            [.. (state?.Profiles ?? []).Where(item => Serves(kind, item)).Select(item => (item.Id, item.Name))],
            local.Files,
            await TransferAsync(request, cancellationToken),
            kind,
            local.Author,
            local.Sources);
    }

    private sealed record LocalState(
        string? Title,
        string? NativeTitle,
        string? Description,
        string? CoverUrl,
        string? Status,
        string? Author,
        string? LibraryUrl,
        string? ReaderUrl,
        List<ReadingLocalFile> Files,
        IReadOnlyList<ReadingSourceAdmin>? Sources);

    private async Task<LocalState> MangaLocalAsync(long workId, CancellationToken cancellationToken)
    {
        var seriesId = await db.WorkSourceLinks.AsNoTracking()
            .Where(link => link.WorkId == workId && link.SourceKind == WorkSourceKind.MangaSeries)
            .Select(link => (Guid?)link.SourceId)
            .FirstOrDefaultAsync(cancellationToken);
        var series = seriesId is { } id ? await new MangaRepository(db).GetSeriesAsync(id, cancellationToken) : null;
        var rows = seriesId is { } sourceId
            ? await db.Database.SqlQuery<FileRow>(
                $"""SELECT "Id", "Number", "VolumeNumber", "Title", "PageCount", "SourcePath", "SourceKind", "SupersededById" FROM "MangaChapters" WHERE "SeriesId" = {sourceId.ToString()}""").ToListAsync(cancellationToken)
            : [];
        var files = rows
            .Select(row => new ReadingLocalFile(Guid.Parse(row.Id), row.Number, row.VolumeNumber, row.Title, row.PageCount, FormatOf(row.SourcePath, row.SourceKind), row.SourcePath, row.SupersededById is not null, row.SupersededById is null ? $"/Manga/Read/{row.Id}" : null, MangaFileQuality.Of(row.SourcePath, row.SourceKind)))
            .OrderBy(file => file.VolumeNumber ?? int.MaxValue)
            .ThenBy(file => file.Number)
            .ThenBy(file => file.Superseded)
            .ToList();
        return new LocalState(
            series?.Title,
            series?.NativeTitle,
            series?.Description,
            series?.CoverImageUrl,
            series?.Status,
            null,
            seriesId is { } library ? $"/Manga/Series/{library}" : null,
            files.FirstOrDefault(file => !file.Superseded) is { } first ? $"/Manga/Read/{first.Id}" : null,
            files,
            null);
    }

    // The published volumes of a Light Novel are the EPUB volumes of its library series, each with the editions it keeps; a public web novel of the Work is listed as its own source.
    private async Task<LocalState> NovelLocalAsync(long workId, CancellationToken cancellationToken)
    {
        var works = await (from link in db.WorkSourceLinks.AsNoTracking()
                           join novel in db.NovelWorks.AsNoTracking() on link.SourceId equals novel.Id
                           where link.WorkId == workId && link.SourceKind == WorkSourceKind.NovelWork
                           orderby novel.SourceProvider == NovelEpubImportService.Provider descending, novel.ImportedAt
                           select novel).ToListAsync(cancellationToken);
        var sources = works
            .Select(novel => new ReadingSourceAdmin(novel.SourceProvider, novel.SourceProvider == NovelEpubImportService.Provider ? "EPUB" : novel.SourceProvider, novel.SourceProvider == NovelEpubImportService.Provider ? null : novel.SourceUrl, novel.SourceProvider != NovelEpubImportService.Provider))
            .ToList();
        var published = works.FirstOrDefault(novel => novel.SourceProvider == NovelEpubImportService.Provider) ?? works.FirstOrDefault();
        if (published is null)
        {
            return new LocalState(null, null, null, null, null, null, null, null, [], sources);
        }

        var ids = works.Select(novel => novel.Id).ToArray();
        var volumes = await db.NovelVolumes.AsNoTracking().Where(volume => ids.Contains(volume.WorkId) && volume.Kind == NovelVolumeKinds.Epub).OrderBy(volume => volume.Number).ToListAsync(cancellationToken);
        var volumeIds = volumes.Select(volume => volume.Id).ToArray();
        var editions = await db.NovelVolumeEditions.AsNoTracking().Where(edition => volumeIds.Contains(edition.VolumeId)).ToListAsync(cancellationToken);
        var chapterCounts = await db.NovelChapters.AsNoTracking().Where(chapter => volumeIds.Contains(chapter.VolumeId)).GroupBy(chapter => chapter.VolumeId).Select(group => new { group.Key, Count = group.Count() }).ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);
        var files = new List<ReadingLocalFile>();
        foreach (var volume in volumes)
        {
            var count = chapterCounts.GetValueOrDefault(volume.Id);
            var own = editions.Where(edition => edition.VolumeId == volume.Id).OrderByDescending(edition => edition.ContentHash == volume.SourceContentHash).ThenByDescending(edition => edition.ImportedAt).ToList();
            if (own.Count == 0)
            {
                files.Add(new ReadingLocalFile(volume.Id, volume.Number, volume.Number, volume.SourceFileName ?? volume.Title ?? $"Volume {volume.Number}", count, "EPUB", volume.SourceStoragePath ?? "", Superseded: false, ReadUrl: null, Quality: "EPUB"));
                continue;
            }

            files.AddRange(own.Select(edition => new ReadingLocalFile(edition.Id, volume.Number, volume.Number, edition.FileName, count, "EPUB", edition.StoragePath ?? "", edition.ContentHash != volume.SourceContentHash, null, edition.Quality)));
        }

        var first = await db.NovelChapters.AsNoTracking().Where(chapter => ids.Contains(chapter.WorkId)).OrderBy(chapter => chapter.Number).Select(chapter => (Guid?)chapter.Id).FirstOrDefaultAsync(cancellationToken);
        return new LocalState(
            published.MetadataTitle ?? published.Title,
            published.MetadataNativeTitle,
            published.MetadataDescription ?? published.Description,
            published.CoverImageUrl,
            published.MetadataStatus,
            published.Author,
            $"/Novels/Work/{published.Id}",
            first is { } chapterId ? $"/Novels/Read/{chapterId}" : null,
            files,
            sources);
    }

    private sealed class FileRow
    {
        public string Id { get; set; } = "";

        public double Number { get; set; }

        public int? VolumeNumber { get; set; }

        public string Title { get; set; } = "";

        public int PageCount { get; set; }

        public string SourcePath { get; set; } = "";

        public string SourceKind { get; set; } = "";

        public string? SupersededById { get; set; }
    }

    private async Task<ReadingTransfer?> TransferAsync(AcquisitionRequest? request, CancellationToken cancellationToken)
    {
        if (request is not { Status: AcquisitionRequestStatus.Downloading or AcquisitionRequestStatus.Importing })
        {
            return null;
        }

        var release = ReadingAcquisitionEngine.ReadPayload(request, ReadingAcquisitionEngine.FallbackTarget(request)).GrabbedRelease;
        if (string.IsNullOrWhiteSpace(release))
        {
            return null;
        }

        var snapshot = operations is not null && request.OperationId is { } operationId ? await operations.GetAsync(operationId, cancellationToken) : null;
        return new ReadingTransfer(request.Status, release, ReadingReleaseParser.Parse(release), request.Status == AcquisitionRequestStatus.Downloading ? snapshot?.ProgressPercent : null);
    }

    public Task<bool> IsAsync(long workId, MediaAcquisitionKind kind, CancellationToken cancellationToken)
    {
        var type = kind == MediaAcquisitionKind.LightNovel ? WorkMediaType.LightNovel : WorkMediaType.Manga;
        return db.Works.AsNoTracking().AnyAsync(item => item.Id == workId && item.MediaType == type, cancellationToken);
    }

    public async Task<(string Provider, string ExternalId)?> IdentityAsync(long workId, CancellationToken cancellationToken)
    {
        var type = await db.Works.AsNoTracking().Where(item => item.Id == workId).Select(item => item.MediaType).FirstOrDefaultAsync(cancellationToken);
        var kind = type == WorkMediaType.LightNovel ? MediaAcquisitionKind.LightNovel : MediaAcquisitionKind.Manga;
        var identity = await db.WorkExternalIdentities.AsNoTracking()
            .Where(item => item.WorkId == workId && item.MediaType == type && (item.Provider == NovelAniListProvider.ProviderKey || (kind == MediaAcquisitionKind.LightNovel && item.Provider == NcodeNovelSourceProvider.ProviderKey)))
            .OrderByDescending(item => item.Provider == NovelAniListProvider.ProviderKey)
            .ThenByDescending(item => item.IsPrimary)
            .Select(item => new { item.Provider, item.ExternalId })
            .FirstOrDefaultAsync(cancellationToken);
        return identity is not null && RequestWorkBinder.IsTrustworthy(kind, identity.Provider, identity.ExternalId) ? (identity.Provider, identity.ExternalId) : null;
    }

    // Whether a profile can take the kind at all: a Manga profile allows a CBZ or ZIP, a Light Novel profile an EPUB.
    public static bool Serves(MediaAcquisitionKind kind, QualityProfile profile) =>
        profile.AllowedQualities.Any(quality => kind == MediaAcquisitionKind.LightNovel
            ? quality.Equals("EPUB", StringComparison.OrdinalIgnoreCase)
            : quality.Equals("CBZ", StringComparison.OrdinalIgnoreCase) || quality.Equals("ZIP", StringComparison.OrdinalIgnoreCase));

    private static string FormatOf(string? path, string sourceKind) =>
        path is not null && Path.GetExtension(path).TrimStart('.').ToUpperInvariant() is { Length: > 0 } extension ? extension : sourceKind.Equals("archive", StringComparison.OrdinalIgnoreCase) ? "ZIP" : "Images";
}
