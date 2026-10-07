using Jularr.Web.Features.Library;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.ReadingAcquisition;
using Jularr.Web.Features.Storage;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Books;

/// <summary>
/// The Books importer behind the shared completed-download dispatcher and the Books inbox
/// scan. A completed download (its own job folder, after the remote-path mapping) is imported
/// as one book, EPUB preferred over PDF; the inbox imports every book in its folder. Source
/// files are only read: EPUBs are parsed into the library and PDFs copied into Jularr's Books
/// files, so the download or inbox can be cleaned up afterwards.
/// </summary>
public sealed class BookCompletedDownloadImportAdapter(
    BookCatalogService books,
    AppDbContext db,
    AnimeImportSettingsStore importSettings,
    IHardLinkCreator hardLinks,
    ILogger<BookCompletedDownloadImportAdapter> logger,
    LibraryRootRoutingService? routing = null)
    : ICompletedDownloadImportAdapter, IMediaInboxImportAdapter
{
    /// <summary>Why a finished download did not become a book; the next release is tried.</summary>
    public const string NoBookFileReason = "The download contained no readable EPUB or PDF.";

    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Book;

    private CompletedDownloadPlacement Placement =>
        new(books.FilesPath, ImportMode.Copy);

    public async Task<CompletedDownloadImportResult> ImportAsync(
        CompletedDownloadImportRequest request,
        CancellationToken cancellationToken)
    {
        CompletedDownloadPlacement? placement = null;
        if (!File.Exists(request.SourcePath) && !Directory.Exists(request.SourcePath))
        {
            // Not the release's fault: an unmapped path or an offline share. Wanted waits and
            // fails with this reason if it never appears, instead of burning other releases.
            return CompletedDownloadImportResult.RetryLater(
                $"The download finished in '{request.SourcePath}', but Jularr cannot read it. Check the remote path mappings.");
        }

        var hint = request.Request is { } acquisition ? Hint(acquisition) : null;
        IReadOnlyList<Guid> imported;
        try
        {
            var settings = await importSettings.LoadAsync(cancellationToken);
            if (routing is not null)
            {
                settings = await routing.WithRoutedLibrariesAsync(settings, cancellationToken);
            }

            var library = settings.LibraryFor(MediaAcquisitionKind.Book);
            if (library is null)
            {
                return CompletedDownloadImportResult.RetryLater(LibraryRootRoutingService.MissingDefaultMessage(LibraryContentType.Book));
            }

            var destination = ReadingLibraryPlacement.ReleaseFolder(
                library.LibraryRoot!,
                request.Request?.Title ?? Path.GetFileNameWithoutExtension(request.SourcePath));
            var mode = settings.ModeFor(MediaAcquisitionKind.Book);
            placement = new CompletedDownloadPlacement(destination, mode);
            new ReadingLibraryPlacement(new ImportFileTransfer(hardLinks)).PlaceBookFiles(
                request.SourcePath,
                destination,
                mode);
            var importSource = destination;
            const bool preserveSourceFiles = true;

            imported = await books.ImportBooksFromPathAsync(
                importSource,
                "download",
                hint,
                singleBook: true,
                cancellationToken,
                preserveSourceFiles);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            // Placing a download that holds no readable book file: the release is unsuitable, not the storage.
            return CompletedDownloadImportResult.RejectRelease(NoBookFileReason);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException)
        {
            logger.LogWarning(
                exception,
                "Books import is waiting for storage for '{SourcePath}'.",
                request.SourcePath);
            return CompletedDownloadImportResult.RetryLater(
                "The Books import is waiting for storage.");
        }

        if (imported.Count == 0)
        {
            return CompletedDownloadImportResult.RejectRelease(NoBookFileReason);
        }

        var works = await db.NovelWorks
            .AsNoTracking()
            .Where(work => imported.Contains(work.Id))
            .Select(work => new { work.Id, Title = work.MetadataTitle ?? work.Title })
            .ToListAsync(cancellationToken);

        if (request.Request is not { } answered || hint is null)
        {
            return CompletedDownloadImportResult.Completed(
                $"Imported {works.Count} book(s).",
                $"/Books/Library/{works[0].Id}",
                placement ?? Placement);
        }

        var match = works.FirstOrDefault(work => SameTitle(work.Title, answered.Title))
            ?? (works.Count == 1 ? works[0] : null);
        if (match is null)
        {
            return CompletedDownloadImportResult.RejectRelease(NoBookFileReason);
        }

        await request.ReportProgressAsync(
            CompletedDownloadImportPhase.MatchingMetadata,
            "Matching imported book with the requested catalog item.",
            placement ?? Placement);
        await books.LinkRequestedWorkAsync(match.Id, hint, cancellationToken);
        return CompletedDownloadImportResult.Completed(
            "Downloaded book imported.",
            $"/Books/Library/{match.Id}",
            placement ?? Placement);
    }

    public async Task<MediaInboxImportResult> ImportInboxAsync(
        string inboxRoot,
        IReadOnlyCollection<string> excludedFolders,
        CancellationToken cancellationToken)
    {
        var imported = await books.ImportBooksFromPathAsync(
            inboxRoot,
            "inbox",
            hint: null,
            singleBook: false,
            cancellationToken,
            excludedFolders: excludedFolders);
        return new MediaInboxImportResult(
            imported.Count,
            imported.Count == 0
                ? "No new books in the inbox."
                : $"Imported {imported.Count} book(s) from the inbox.",
            imported.Count == 1 ? $"/Books/Library/{imported[0]}" : null);
    }

    /// <summary>What a request knows about its book, for naming and linking the import.</summary>
    public static BookImportHint Hint(AcquisitionRequest request)
    {
        var payload = BookAcquisitionExecutor.ReadPayload(request);
        return new BookImportHint(payload.CatalogId, payload.Title, payload.Author, request.CoverImageUrl);
    }

    public static bool SameTitle(string imported, string requested)
    {
        static string Normalize(string value) =>
            new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        var left = Normalize(imported);
        var right = Normalize(requested);
        return left.Length > 0 && right.Length > 0 && (left.Contains(right) || right.Contains(left));
    }
}
