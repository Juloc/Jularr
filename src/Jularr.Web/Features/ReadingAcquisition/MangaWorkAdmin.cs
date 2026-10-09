using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Novels;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.ReadingAcquisition;

public sealed record MangaIdentityAdmin(string Provider, string ExternalId, bool IsPrimary);

/// <summary>One chapter or volume file of the library; whether its path can be read now is decided for the rows a page shows, not for all of them.</summary>
public sealed record MangaLocalFile(Guid Id, double Number, int? VolumeNumber, string Name, int PageCount, string Format, string SourcePath);

/// <summary>What the Admin Manga page shows: the title, its provider identity, the volumes and chapters with what the library holds, the request, the profile and the files.</summary>
public sealed record MangaWorkAdminView(
    long WorkId,
    string Title,
    string? NativeTitle,
    IReadOnlyList<string> Aliases,
    string? Description,
    string? CoverUrl,
    string? Status,
    int? Year,
    IReadOnlyList<MangaIdentityAdmin> Identities,
    Guid? SeriesId,
    Guid? FirstChapterId,
    ReadingCoverageView Coverage,
    AcquisitionRequest? Request,
    IReadOnlyList<AcquisitionRequest> History,
    string ProfileId,
    string ProfileName,
    string? AssignedProfileId,
    IReadOnlyList<(string Id, string Name)> Profiles,
    IReadOnlyList<MangaLocalFile> Files)
{
    public bool RequestIsOpen => Request is { Status: not (AcquisitionRequestStatus.Completed or AcquisitionRequestStatus.Rejected or AcquisitionRequestStatus.Failed) };

    public bool RequestIsWaiting => Request is { Status: AcquisitionRequestStatus.Approved };

    public bool RequestFailed => Request is { Status: AcquisitionRequestStatus.Failed };

    public IReadOnlyList<string> Formats => [.. Files.Select(file => file.Format).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Everything the structure says is wanted, installed or missing: the one summary of the header.</summary>
    public (int Installed, int Total) VolumeCount => (Coverage.Volumes.Count(volume => volume.State == ReadingCoverageState.Installed), Coverage.Volumes.Count);

    public (int Installed, int Total) ChapterCount
    {
        get
        {
            var all = Coverage.Volumes.SelectMany(volume => volume.Chapters).Concat(Coverage.LooseChapters).ToArray();
            return (all.Count(chapter => chapter.Installed), all.Length);
        }
    }

    /// <summary>Whether a search can find something: a unit is missing (whether or not it is monitored), or while the structure is unknown nothing is in the library yet.</summary>
    public bool CanSearch => (!RequestIsOpen || RequestIsWaiting)
        && (Coverage.HasStructure
            ? Coverage.Volumes.Any(volume => volume.State != ReadingCoverageState.Installed) || Coverage.LooseChapters.Any(chapter => !chapter.Installed)
            : Coverage.LocalChapters == 0);
}

/// <summary>The read model of the Admin Manga page, built from the canonical Work, the coverage calculation and the library's own series.</summary>
public sealed class MangaWorkAdminQuery(AppDbContext db, ReadingCoverageService coverage, AcquisitionAccessStore requests, QualityProfileStore? profiles = null)
{
    public async Task<MangaWorkAdminView?> GetAsync(long workId, CancellationToken cancellationToken)
    {
        var work = await db.Works.AsNoTracking().Where(item => item.Id == workId && item.MediaType == WorkMediaType.Manga).Select(item => new { item.CanonicalTitle, item.Year }).SingleOrDefaultAsync(cancellationToken);
        if (work is null)
        {
            return null;
        }

        var identities = await db.WorkExternalIdentities.AsNoTracking()
            .Where(item => item.WorkId == workId && item.MediaType == WorkMediaType.Manga && item.Provider == NovelAniListProvider.ProviderKey)
            .OrderByDescending(item => item.IsPrimary)
            .Select(item => new MangaIdentityAdmin(item.Provider, item.ExternalId, item.IsPrimary))
            .ToListAsync(cancellationToken);
        var names = await db.WorkTitles.AsNoTracking().Where(item => item.WorkId == workId).OrderBy(item => item.CreatedAt).Select(item => new { item.Value, item.TitleType }).ToListAsync(cancellationToken);
        var seriesId = await db.WorkSourceLinks.AsNoTracking()
            .Where(link => link.WorkId == workId && link.SourceKind == WorkSourceKind.MangaSeries)
            .Select(link => (Guid?)link.SourceId)
            .FirstOrDefaultAsync(cancellationToken);
        var repository = new MangaRepository(db);
        var series = seriesId is { } id ? await repository.GetSeriesAsync(id, cancellationToken) : null;
        var sources = seriesId is { } sourceId ? await repository.GetChapterSourcesAsync(sourceId, cancellationToken) : [];
        var paths = sources.ToDictionary(source => source.Id, source => source.SourcePath);
        var files = (series?.Chapters ?? [])
            .Select(chapter => new MangaLocalFile(
                chapter.Id,
                chapter.Number,
                chapter.VolumeNumber,
                chapter.Title,
                chapter.PageCount,
                FormatOf(paths.GetValueOrDefault(chapter.Id), chapter.SourceKind),
                paths.GetValueOrDefault(chapter.Id) ?? ""))
            .OrderBy(file => file.VolumeNumber ?? int.MaxValue)
            .ThenBy(file => file.Number)
            .ToList();

        var identity = identities.FirstOrDefault();
        var request = identity is null ? null : await requests.FindLatestAsync(MediaAcquisitionKind.Manga, identity.Provider, identity.ExternalId, cancellationToken);
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

        var profile = profiles is null ? null : await profiles.ResolveAsync(MediaAcquisitionKind.Manga, workId, cancellationToken);
        var state = profiles is null ? null : await profiles.LoadAsync(cancellationToken);
        return new MangaWorkAdminView(
            workId,
            series?.Title ?? work.CanonicalTitle,
            series?.NativeTitle ?? names.FirstOrDefault(name => name.TitleType == WorkTitleType.Native)?.Value,
            [.. names.Select(name => name.Value).Where(value => !value.Equals(series?.Title ?? work.CanonicalTitle, StringComparison.OrdinalIgnoreCase) && !value.Equals(series?.NativeTitle, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)],
            series?.Description,
            series?.CoverImageUrl,
            series?.Status,
            work.Year,
            identities,
            seriesId,
            files.FirstOrDefault()?.Id,
            view,
            request,
            history,
            profile?.Id ?? "",
            profile?.Name ?? "",
            state?.WorkAssignments.GetValueOrDefault(workId.ToString("D")),
            [.. (state?.Profiles ?? []).Where(ServesManga).Select(item => (item.Id, item.Name))],
            files);
    }

    /// <summary>The provider identity a request of the Work is made with, or null when it has none the request pipeline trusts.</summary>
    public async Task<(string Provider, string ExternalId)?> IdentityAsync(long workId, CancellationToken cancellationToken)
    {
        var identity = await db.WorkExternalIdentities.AsNoTracking()
            .Where(item => item.WorkId == workId && item.MediaType == WorkMediaType.Manga && item.Provider == NovelAniListProvider.ProviderKey)
            .OrderByDescending(item => item.IsPrimary)
            .Select(item => new { item.Provider, item.ExternalId })
            .FirstOrDefaultAsync(cancellationToken);
        return identity is not null && RequestWorkBinder.IsTrustworthy(MediaAcquisitionKind.Manga, identity.Provider, identity.ExternalId) ? (identity.Provider, identity.ExternalId) : null;
    }

    /// <summary>Whether a profile can take a Manga at all: it allows a CBZ or a ZIP.</summary>
    public static bool ServesManga(QualityProfile profile) =>
        profile.AllowedQualities.Any(quality => quality.Equals("CBZ", StringComparison.OrdinalIgnoreCase) || quality.Equals("ZIP", StringComparison.OrdinalIgnoreCase));

    private static string FormatOf(string? path, string sourceKind) =>
        path is not null && Path.GetExtension(path).TrimStart('.').ToUpperInvariant() is { Length: > 0 } extension ? extension : sourceKind.Equals("archive", StringComparison.OrdinalIgnoreCase) ? "ZIP" : "Images";
}
