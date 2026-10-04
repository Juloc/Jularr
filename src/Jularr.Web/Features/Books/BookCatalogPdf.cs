using System.Globalization;
using System.Security.Cryptography;
using Jularr.Web.Features.Novels;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Books;

/// <summary>
/// What a Books acquisition knows about the book a download belongs to. It names PDFs (which
/// rarely carry usable metadata), links request imports to their catalog entry, and can target an
/// existing local work so a monitored replacement/upgrade adds a new edition instead of a duplicate work.
/// </summary>
public sealed record BookImportHint(
    string CatalogId,
    string Title,
    string? Author,
    string? CoverImageUrl,
    Guid? ExistingWorkId = null);

/// <summary>
/// The file Jularr keeps for a Books work (today: the PDF of a PDF book). <see cref="PageCount"/>
/// is the number of chapters, which are the logical pages of a PDF in reading order.
/// </summary>
public sealed record BookStoredFile(
    Guid WorkId,
    string Path,
    string FileName,
    string Format,
    string MediaType,
    long SizeBytes,
    int PageCount,
    string ContentHash);

public sealed partial class BookCatalogService
{
    /// <summary>
    /// The provider of Books catalog requests (Add book dialog), and the metadata provider of
    /// works linked to the catalog entry such a request asked for.
    /// </summary>
    public const string CatalogRequestProvider = "books-catalog";

    private const long MaxPdfBytes = 500L * 1024 * 1024;

    /// <summary>
    /// Where imported PDFs are kept (<c>Books:FilesPath</c>, default <c>/data/books/files</c>).
    /// EPUBs are parsed into chapters; a PDF is also read into chapters (one per page), but the
    /// reader renders the file itself, so it is kept.
    /// </summary>
    public string FilesPath =>
        Path.GetFullPath(FirstNonEmpty(configuration["Books:FilesPath"], null)
            ?? Path.Combine("/data", "books", "files"));

    /// <summary>Where extracted covers are kept (<c>Books:CoversPath</c>, default <c>/data/books/covers</c>).</summary>
    private string CoversPath =>
        Path.GetFullPath(FirstNonEmpty(configuration["Books:CoversPath"], null)
            ?? Path.Combine("/data", "books", "covers"));

    /// <summary>
    /// Imports every supported book file at <paramref name="path"/>: the file itself, or the EPUB
    /// and PDF files below the folder including subfolders (SABnzbd puts each job in its own
    /// folder). Damaged or unreadable files are skipped, so one bad file never blocks the rest.
    /// <paramref name="hint"/> names a single imported book and can target an existing work. Files below
    /// <paramref name="excludedFolders"/> (another media type's inbox nested in this folder) are
    /// left alone.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ImportBooksFromPathAsync(
        string path,
        string sourceKind,
        BookImportHint? hint,
        bool singleBook,
        CancellationToken cancellationToken,
        bool preserveSourceFiles = false,
        IReadOnlyCollection<string>? excludedFolders = null)
    {
        var excluded = (excludedFolders ?? [])
            .Select(folder => Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToArray();
        var found = File.Exists(path)
            ? [path]
            : Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Where(file => !excluded.Any(folder => Path.GetFullPath(file).StartsWith(folder, StringComparison.Ordinal)))
                    .Take(2000)
                : throw new InvalidOperationException($"'{path}' is not available.");
        var files = BookFileFormats.Select(found, singleBook).Take(200).ToArray();

        var imported = new List<Guid>(files.Length);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var fileName = Path.GetFileName(file);
                if (BookFileFormats.FromPath(file) == BookFileFormats.Pdf)
                {
                    imported.Add(await ImportPdfFileAsync(
                        file,
                        fileName,
                        sourceKind,
                        files.Length == 1 ? hint : null,
                        cancellationToken,
                        preserveSourceFiles));
                    continue;
                }

                await using var stream = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 81920,
                    useAsync: true);
                imported.Add(await ImportEpubStreamAsync(
                    stream,
                    fileName,
                    sourceKind,
                    sourceKind + "://" + Uri.EscapeDataString(fileName),
                    cancellationToken,
                    preserveSourceFiles ? Path.GetFullPath(file) : null,
                    files.Length == 1 ? hint?.ExistingWorkId : null));
            }
            catch (IOException)
            {
                // A downloader may still be moving/writing this file; the next scan retries it.
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
            {
                // Not a readable EPUB/PDF (damaged, encrypted, too large): the book is not imported.
            }
        }

        return imported
            .Distinct()
            .ToArray();
    }

    public async Task<Guid> ImportUploadedPdfAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(FilesPath);
        var upload = Path.Combine(FilesPath, $"upload-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var target = new FileStream(upload, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await CopyBoundedAsync(stream, target, MaxPdfBytes, cancellationToken);
            }

            return await ImportPdfFileAsync(upload, Path.GetFileName(fileName), "upload", hint: null, cancellationToken);
        }
        finally
        {
            File.Delete(upload);
        }
    }

    /// <summary>
    /// Imports one PDF as a Books work like any EPUB: the file is copied into
    /// <see cref="FilesPath"/>, every logical page becomes a chapter carrying its extracted text without
    /// page furniture (see <see cref="PdfDocumentAnalyzer"/>; physical pages that repeat an earlier page
    /// share its chapter), and page 1 gives the cover when nothing better is known. Nothing is parsed
    /// as EPUB and nothing is converted.
    /// </summary>
    public async Task<Guid> ImportPdfFileAsync(
        string path,
        string fileName,
        string sourceKind,
        BookImportHint? hint,
        CancellationToken cancellationToken,
        bool preserveSourceFile = false)
    {
        var length = new FileInfo(path).Length;
        if (length > MaxPdfBytes)
        {
            throw new InvalidOperationException("PDF exceeds the 500 MB import limit.");
        }

        if (preserveSourceFile)
        {
            var sourcePath = Path.GetFullPath(path);
            var sourceHash = await ValidateAndHashPdfAsync(sourcePath, fileName, cancellationToken);
            return await SavePdfWorkAsync(
                sourcePath,
                fileName,
                sourceKind,
                sourceHash,
                length,
                hint,
                cancellationToken);
        }

        Directory.CreateDirectory(FilesPath);
        var temporary = Path.Combine(FilesPath, $"import-{Guid.NewGuid():N}.tmp");
        string hash;
        try
        {
            await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true))
            {
                var head = new byte[1024];
                var read = await source.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
                if (!PdfDocumentReader.HasPdfHeader(head.AsSpan(0, read)))
                {
                    throw new InvalidOperationException($"'{fileName}' is not a PDF file.");
                }

                source.Position = 0;
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        hasher.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }

                hash = Convert.ToHexString(hasher.GetHashAndReset());
            }

            var stored = Path.Combine(FilesPath, hash[..48].ToLowerInvariant() + ".pdf");
            if (File.Exists(stored))
            {
                File.Delete(temporary);
            }
            else
            {
                File.Move(temporary, stored);
            }

            return await SavePdfWorkAsync(stored, fileName, sourceKind, hash, length, hint, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<string> ValidateAndHashPdfAsync(
        string path,
        string fileName,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            useAsync: true);
        var head = new byte[1024];
        var read = await source.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
        if (!PdfDocumentReader.HasPdfHeader(head.AsSpan(0, read)))
        {
            throw new InvalidOperationException($"'{fileName}' is not a PDF file.");
        }

        source.Position = 0;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    private async Task<Guid> SavePdfWorkAsync(
        string storedPath,
        string fileName,
        string sourceKind,
        string contentHash,
        long sizeBytes,
        BookImportHint? hint,
        CancellationToken cancellationToken)
    {
        var sourceKey = CleanSourceKey("pdf-" + contentHash[..48]);
        var content = sizeBytes <= PdfDocumentReader.MaxReadBytes
            ? PdfDocumentReader.Read(await File.ReadAllBytesAsync(storedPath, cancellationToken))
            : PdfDocumentContent.Empty;

        var identity = PdfBookIdentity.Resolve(content.Title, content.Author, fileName);

        NovelWork? work;
        if (hint?.ExistingWorkId is Guid targetWorkId)
        {
            work = await db.NovelWorks
                .SingleOrDefaultAsync(
                    x => x.Id == targetWorkId
                        && x.SourceProvider == ImportedBookProvider,
                    cancellationToken)
                ?? throw new InvalidOperationException("The target Books work no longer exists.");
        }
        else
        {
            work = await db.NovelWorks
                .SingleOrDefaultAsync(
                    x => x.SourceProvider == ImportedBookProvider
                        && x.SourceKey == sourceKey,
                    cancellationToken);
            if (work is not null)
            {
                return work.Id;
            }

            var title = FirstNonEmpty(hint?.Title, identity.Title)!;
            work = new NovelWork
            {
                SourceProvider = ImportedBookProvider,
                SourceKey = sourceKey,
                SourceUrl = Truncate(sourceKind + "://" + Uri.EscapeDataString(fileName), 2048),
                Title = Truncate(title, 500),
                Author = TruncateNullable(FirstNonEmpty(hint?.Author, identity.Author), 300),
                MetadataTitle = Truncate(title, 500),
                CoverImageUrl = null,
                MetadataStatus = "IMPORTED",
                ImportedAt = DateTime.UtcNow
            };
            db.NovelWorks.Add(work);
        }
        if (string.IsNullOrWhiteSpace(work.Author))
        {
            work.Author = TruncateNullable(FirstNonEmpty(hint?.Author, identity.Author), 300);
        }

        work.SourceUrl = Truncate(sourceKind + "://" + Uri.EscapeDataString(fileName), 2048);
        work.Format = BookFileFormats.Pdf + ":" + NormalizeSourceLanguage(content.Language);
        work.MetadataStatus = "IMPORTED";
        work.UpdatedAt = DateTime.UtcNow;

        if (await TryPersistPreferredCoverAsync(
                work.Id,
                work.Title,
                work.Author,
                isbn10: null,
                isbn13: null,
                embeddedCover: content.CoverJpeg,
                embeddedMediaType: content.CoverJpeg is null ? null : "image/jpeg",
                catalogFallback: hint?.CoverImageUrl,
                knownStoragePath: storedPath,
                cancellationToken: cancellationToken))
        {
            work.CoverImageUrl = $"/Books/Cover/{work.Id}";
        }

        // Logical pages are the chapters, in reading order, through the canonical Novel volume write path;
        // progress, bookmarks and "continue reading" work exactly as for EPUB chapters.
        var pages = content.Pages.Count > 0 ? content.Pages : [new PdfPageText(1, "")];
        var analysis = PdfDocumentAnalyzer.Analyze(pages, contentHash);
        var volume = await NovelVolumeContent.EnsureImplicitVolumeAsync(db, work, NovelVolumeKinds.Book, cancellationToken);
        await CreateDerivedDocumentStore().SaveAsync(analysis.Document, cancellationToken);
        await SyncPdfChaptersAsync(work, volume, analysis, cancellationToken);

        await UpsertEditionAndFileAsync(
            work,
            new BookEditionFacts(work.Title, work.Author, BookFileFormats.Language(work.Format), null, null, null, null),
            sourceKey,
            work.SourceUrl,
            metadataProvider: null,
            metadataExternalId: null,
            fileName,
            sourceKind,
            contentHash,
            sizeBytes,
            BookFileFormats.Pdf,
            BookFileFormats.PdfMediaType,
            cancellationToken,
            storagePath: storedPath);

        return work.Id;
    }

    /// <summary>
    /// The file Jularr keeps for a Books work, for the reader's file endpoint; null for works
    /// without one (EPUBs are parsed and not kept) or when the file is gone.
    /// </summary>
    public async Task<BookStoredFile?> GetStoredFileAsync(Guid workId, CancellationToken cancellationToken)
    {
        var file = await (
            from candidate in db.BookFiles.AsNoTracking()
            join edition in db.BookEditions.AsNoTracking() on candidate.EditionId equals edition.Id
            join work in db.NovelWorks.AsNoTracking() on edition.WorkId equals work.Id
            where edition.WorkId == workId
                && work.SourceProvider == ImportedBookProvider
                && candidate.StoragePath != null
            orderby edition.IsPrimary descending, candidate.IsPrimary descending, candidate.ImportedAt descending
            select candidate).FirstOrDefaultAsync(cancellationToken);
        if (file?.StoragePath is not { } path || !File.Exists(path))
        {
            return null;
        }

        var pages = await db.NovelChapters.CountAsync(x => x.WorkId == workId, cancellationToken);
        return new BookStoredFile(workId, path, file.FileName, file.Format, file.MediaType, file.SizeBytes, pages, file.ContentHash);
    }

    /// <summary>
    /// Links an imported work to the catalog entry its request asked for, so the Add book dialog
    /// finds it by catalog id; fills a missing author or cover from the request. A PDF takes the
    /// requested book's catalog title: its own Info title is file metadata, often a producer or
    /// library label ("The Project Gutenberg eBook #…") rather than the book's name.
    /// </summary>
    public async Task LinkRequestedWorkAsync(Guid workId, BookImportHint hint, CancellationToken cancellationToken)
    {
        var work = await db.NovelWorks.SingleAsync(x => x.Id == workId, cancellationToken);
        var catalogId = TruncateNullable(hint.CatalogId, 200);
        if (catalogId is not null
            && work.MetadataExternalId is null
            && !await db.NovelWorks.AnyAsync(
                x => x.MetadataProvider == CatalogRequestProvider && x.MetadataExternalId == catalogId,
                cancellationToken))
        {
            work.MetadataProvider = CatalogRequestProvider;
            work.MetadataExternalId = catalogId;
            if (BookFileFormats.IsPdf(work) && !string.IsNullOrWhiteSpace(hint.Title))
            {
                work.Title = Truncate(hint.Title.Trim(), 500);
                work.MetadataTitle = work.Title;
            }
        }

        if (string.IsNullOrWhiteSpace(work.Author))
        {
            work.Author = TruncateNullable(hint.Author, 300);
        }

        if (string.IsNullOrWhiteSpace(work.CoverImageUrl)
            && await TryPersistPreferredCoverAsync(
                work.Id,
                work.Title,
                work.Author,
                isbn10: null,
                isbn13: null,
                embeddedCover: null,
                embeddedMediaType: null,
                catalogFallback: hint.CoverImageUrl,
                knownStoragePath: null,
                cancellationToken: cancellationToken))
        {
            work.CoverImageUrl = $"/Books/Cover/{work.Id}";
        }

        work.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The stored files of a work that only Jularr keeps (PDFs); removed with the work.</summary>
    private Task<List<string>> StoredFilePathsAsync(Guid workId, CancellationToken cancellationToken) =>
        (from file in db.BookFiles.AsNoTracking()
         join edition in db.BookEditions.AsNoTracking() on file.EditionId equals edition.Id
         where edition.WorkId == workId && file.StoragePath != null
         select file.StoragePath!).ToListAsync(cancellationToken);

    private async Task DeleteStoredFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var root = FilesPath + Path.DirectorySeparatorChar;
        foreach (var path in paths)
        {
            // Only files Jularr copied into its own store; a file another work still uses stays.
            if (!Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal)
                || await db.BookFiles.AsNoTracking().AnyAsync(x => x.StoragePath == path, cancellationToken))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>A logical page's chapter title: its first line of text, or its number for pages without text.</summary>
    private static string PageTitle(int pageNumber, string text)
    {
        var firstLine = text.Split('\n', 2)[0].Trim();
        if (firstLine.Length == 0)
        {
            return pageNumber.ToString(CultureInfo.InvariantCulture);
        }

        return firstLine.Length <= 80 ? firstLine : firstLine[..79].TrimEnd() + "…";
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException("PDF exceeds the 500 MB import limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    /// <summary>The edition metadata of an imported file, whatever its format.</summary>
    private sealed record BookEditionFacts(
        string Title,
        string? Author,
        string? Language,
        string? Isbn10,
        string? Isbn13,
        string? Publisher,
        string? PublishedDate)
    {
        public static BookEditionFacts From(ParsedEpubBook parsed) =>
            new(parsed.Title, parsed.Author, parsed.Language, parsed.Isbn10, parsed.Isbn13, parsed.Publisher, parsed.PublishedDate);
    }
}
