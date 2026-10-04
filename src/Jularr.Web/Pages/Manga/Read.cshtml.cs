using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Manga;

/// <summary>
/// A run of consecutive chapters with the same volume number, as listed in the
/// reader's contents panel. <see cref="Number"/> is null for chapters that the
/// import could not place in a volume.
/// </summary>
public sealed record MangaReaderVolume(
    int? Number,
    IReadOnlyList<MangaChapterItem> Chapters,
    bool ContainsCurrent)
{
    public MangaChapterItem First => Chapters[0];
    public MangaChapterItem Last => Chapters[^1];
}

public sealed class ReadModel(
    AppDbContext db,
    CurrentAccountContext account,
    WorkQueryService workQueries,
    LegacyWorkBridge workBridge) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public MangaChapterRead Chapter { get; private set; } = null!;
    public IReadOnlyList<MangaChapterItem> Chapters { get; private set; } = [];
    public IReadOnlyList<MangaReaderVolume> Volumes { get; private set; } = [];
    public IReadOnlyList<MangaBookmarkItem> Bookmarks { get; private set; } = [];
    public Guid? PreviousChapterId { get; private set; }
    public Guid? NextChapterId { get; private set; }
    public MangaChapterItem? PreviousChapter { get; private set; }
    public MangaChapterItem? NextChapter { get; private set; }
    public int InitialPage { get; private set; }
    public MangaReaderPreset ReaderSettings { get; private set; } = null!;
    public string ProfileId => account.ProfileId;

    /// <summary>Number of the last chapter, the denominator of the chapter pill.</summary>
    public string LastChapterNumber =>
        FormatNumber(Chapters.Count == 0 ? Chapter.Number : Chapters[^1].Number);

    public static string FormatNumber(double number) =>
        number.ToString("0.##", CultureInfo.CurrentCulture);

    /// <summary>
    /// The chapter title when it adds something to "Chapter 12": imports without
    /// a usable file name are titled "Chapter 12" and would only repeat it.
    /// </summary>
    public static string? DistinctTitle(double number, string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var generic = string.Create(
            CultureInfo.InvariantCulture,
            $"Chapter {number:0.##}");
        return string.Equals(trimmed, generic, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, FormatNumber(number), StringComparison.Ordinal)
            ? null
            : trimmed;
    }

    public static IReadOnlyList<MangaReaderVolume> GroupVolumes(
        IReadOnlyList<MangaChapterItem> chapters,
        Guid currentChapterId)
    {
        var volumes = new List<MangaReaderVolume>();
        var run = new List<MangaChapterItem>();

        void Flush()
        {
            if (run.Count == 0)
            {
                return;
            }

            volumes.Add(new MangaReaderVolume(
                run[0].VolumeNumber,
                run.ToArray(),
                run.Any(x => x.Id == currentChapterId)));
            run.Clear();
        }

        foreach (var chapter in chapters)
        {
            if (run.Count > 0 && run[0].VolumeNumber != chapter.VolumeNumber)
            {
                Flush();
            }

            run.Add(chapter);
        }

        Flush();
        return volumes;
    }

    // "page" is also the Razor Pages route value (the page path) and model
    // binding reads route values before the query string; the page index is
    // therefore bound from the query explicitly (?page= resume, page images).
    public async Task<IActionResult> OnGetAsync(
        Guid id,
        [FromQuery(Name = "page")] int? page,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var repository = new MangaRepository(db);
        var chapter = await repository.GetChapterAsync(id, cancellationToken);
        if (chapter is null)
        {
            return NotFound();
        }

        Chapter = chapter;
        Chapters = await repository.GetChaptersAsync(
            chapter.SeriesId,
            cancellationToken);
        Bookmarks = await repository.GetBookmarksAsync(
            account.ProfileId,
            chapter.SeriesId,
            cancellationToken);

        (PreviousChapterId, NextChapterId) =
            await repository.GetAdjacentChapterIdsAsync(
                chapter.SeriesId,
                chapter.Number,
                cancellationToken);
        PreviousChapter = Chapters.FirstOrDefault(x => x.Id == PreviousChapterId);
        NextChapter = Chapters.FirstOrDefault(x => x.Id == NextChapterId);
        Volumes = GroupVolumes(Chapters, chapter.Id);

        var progress = await repository.GetProgressAsync(
            account.ProfileId,
            chapter.SeriesId,
            cancellationToken);

        var workId = await workQueries.ResolveWorkForSourceAsync(
            WorkSourceKind.MangaSeries,
            chapter.SeriesId,
            cancellationToken);

        // Local-only: the reader never waits on AniList. Remote progress and
        // the Operations-backed sync are on the series page's lazy card.
        ReaderSettings = await MangaReaderPreferences.GetAsync(
            db,
            account.ProfileId,
            workId,
            cancellationToken);

        var requested = page
            ?? (progress?.ChapterId == chapter.Id ? progress.PageIndex : 0);

        InitialPage = Math.Clamp(
            requested,
            0,
            Math.Max(0, chapter.PageCount - 1));

        return Page();
    }

    public async Task<IActionResult> OnPostPreferenceAsync(
        Guid id,
        string? scope,
        string? changedKey,
        MangaReaderPreferenceInput input,
        CancellationToken cancellationToken)
    {
        var repository = new MangaRepository(db);
        var chapter = await repository.GetChapterAsync(id, cancellationToken);
        if (chapter is null)
        {
            return NotFound();
        }

        var isTypeDefault = string.Equals(
            scope,
            "media",
            StringComparison.OrdinalIgnoreCase);
        Guid? workId = await workQueries.ResolveWorkForSourceAsync(
            WorkSourceKind.MangaSeries,
            chapter.SeriesId,
            cancellationToken);

        if (!isTypeDefault && workId is null)
        {
            workId = await workBridge.EnsureWorkForMangaSeriesAsync(
                chapter.SeriesId,
                chapter.SeriesTitle,
                nativeTitle: null,
                aniListId: null,
                cancellationToken);
        }

        await MangaReaderPreferences.SaveAsync(
            db,
            account.ProfileId,
            isTypeDefault ? null : workId,
            input,
            changedKey,
            cancellationToken);

        var resolved = await MangaReaderPreferences.GetAsync(
            db,
            account.ProfileId,
            workId,
            cancellationToken);
        return new JsonResult(resolved);
    }

    public async Task<IActionResult> OnPostResetPreferenceAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var repository = new MangaRepository(db);
        var chapter = await repository.GetChapterAsync(id, cancellationToken);
        if (chapter is null)
        {
            return NotFound();
        }

        var workId = await workQueries.ResolveWorkForSourceAsync(
            WorkSourceKind.MangaSeries,
            chapter.SeriesId,
            cancellationToken);
        if (workId is Guid resolvedWorkId)
        {
            await MangaReaderPreferences.ResetWorkAsync(
                db,
                account.ProfileId,
                resolvedWorkId,
                cancellationToken);
        }

        var settings = await MangaReaderPreferences.GetAsync(
            db,
            account.ProfileId,
            workId,
            cancellationToken);

        return new JsonResult(settings);
    }

    public async Task<IActionResult> OnGetPageAsync(
        Guid id,
        [FromQuery(Name = "page")] int page,
        CancellationToken cancellationToken)
    {
        var repository = new MangaRepository(db);
        var item = await repository.GetPageAsync(
            id,
            page,
            cancellationToken);

        if (item is null)
        {
            return NotFound();
        }

        var root = Path.GetFullPath(MangaImportService.CacheRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var cachedPath = Path.GetFullPath(item.CachedPath);

        if (!cachedPath.StartsWith(root, StringComparison.Ordinal) ||
            !System.IO.File.Exists(cachedPath))
        {
            return NotFound();
        }

        return new PhysicalFileResult(cachedPath, item.MimeType)
        {
            EnableRangeProcessing = true
        };
    }

    public async Task<IActionResult> OnPostProgressAsync(
        Guid id,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var repository = new MangaRepository(db);
        var chapter = await repository.GetChapterAsync(id, cancellationToken);
        if (chapter is null)
        {
            return NotFound();
        }

        await repository.SaveProgressAsync(
            account.ProfileId,
            chapter,
            pageIndex,
            cancellationToken);

        return new OkResult();
    }

    public async Task<IActionResult> OnPostBookmarkAsync(
        Guid id,
        int pageIndex,
        string? label,
        CancellationToken cancellationToken)
    {
        var repository = new MangaRepository(db);
        var chapter = await repository.GetChapterAsync(id, cancellationToken);
        if (chapter is null)
        {
            return NotFound();
        }

        var bookmark = await repository.AddBookmarkAsync(
            account.ProfileId,
            chapter,
            pageIndex,
            label,
            cancellationToken);

        return new JsonResult(new
        {
            bookmark.Id,
            bookmark.ChapterId,
            bookmark.PageIndex,
            bookmark.Label
        });
    }

    public async Task<IActionResult> OnPostRemoveBookmarkAsync(
        Guid id,
        Guid bookmarkId,
        CancellationToken cancellationToken)
    {
        var repository = new MangaRepository(db);
        await repository.RemoveBookmarkAsync(
            account.ProfileId,
            bookmarkId,
            cancellationToken);
        return new OkResult();
    }
}
