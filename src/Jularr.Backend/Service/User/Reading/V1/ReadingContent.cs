using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Djvu;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Djvu;
using Jularr.Integrations.Logic.Pdf;
using Jularr.Integrations.Logic.Storage;
using Jularr.Integrations.Logic.Usenet;

namespace Jularr.Backend.Service.User.Reading.V1
{
    // Prepares books and comics from storage: text, structure and translations into the database, pictures and pages the server draws onto disk (ReadingFiles);
    // an EPUB's pictures and a CBZ's pages are read from the file itself when they are shown. I/O stays outside transactions.
    internal sealed partial class ReadingContent
    {
        [GeneratedRegex("<[^>]+>")]
        private static partial Regex Tags();

        private const long BatchBytes = 8L * 1024 * 1024;

        // A DjVu book is drawn a few pages ahead, one batch on the server at a time; a book on a direct share is copied aside up to this size.
        private const int PagesAhead = 4;
        private const long MaximumDjvuBytes = 2L * 1024 * 1024 * 1024;
        private static readonly SemaphoreSlim DjvuGate = new(1, 1);

        // A book's pictures drawn again because its files left the disk: one book at a time.
        private static readonly SemaphoreSlim RedrawGate = new(1, 1);

        private readonly StorageAccess _storage;
        private readonly ArchiveTools _archives;
        private readonly DjvuTools _djvu;
        private readonly ReadingFiles _files;
        private readonly TimeProvider _time;

        public ReadingContent(StorageAccess storage, ArchiveTools archives, DjvuTools djvu, ReadingFiles files, TimeProvider time)
        {
            _storage = storage;
            _archives = archives;
            _djvu = djvu;
            _files = files;
            _time = time;
        }

        public static string SectionPath(int index) => ":section/" + index.ToString("D5", CultureInfo.InvariantCulture);

        public static bool IsDjvu(string relativePath) => Path.GetExtension(relativePath).ToLowerInvariant() is ".djvu" or ".djv";

        // Pictures the server makes (a PDF's, a book read through a copy made on opening, a comic unpacked by 7-Zip) are kept on disk when the book is prepared.
        public static bool KeepsFiles(string relativePath) => relativePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || BookConversion.IsConverted(relativePath)
            || ComicPages.IsComic(relativePath) && !ComicPages.IsZip(relativePath);

        // A scanned PDF's pages the recognizer has read go into the text.
        public async Task<long> OpenAsync(LogicContext context, ReadingSourceData source, CancellationToken ct)
        {
            // A prepared book needs no storage once the pictures the server makes are on disk; with the storage offline its text still reads.
            if (source is { DocumentId: { } cached, IsCached: true } && (source.IsMissing || !KeepsFiles(source.RelativePath) || _files.IsComplete(cached)))
            {
                return await TouchAsync(context, cached, ct);
            }

            if (source.IsMissing)
            {
                throw new ServiceUnavailableException("reading_unavailable");
            }

            Stream file;
            try
            {
                file = await OpenFileAsync(context, source.StorageMountId, source.RootRelativePath, source.RelativePath, ct);
            }
            catch (ServiceUnavailableException) when (source is { DocumentId: { } offline, IsCached: true })
            {
                return await TouchAsync(context, offline, ct);
            }

            await using (file)
            {
                if (ComicPages.IsComic(source.RelativePath))
                {
                    return await OpenComicAsync(context, source, file, ct);
                }

                if (IsDjvu(source.RelativePath))
                {
                    return await OpenDjvuAsync(context, source, file, ct);
                }

                if (source.RelativePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    return await OpenPdfAsync(context, source, file, ct);
                }

                byte[] hash;
                Stream epub = file;
                var converted = BookConversion.IsConverted(source.RelativePath);
                if (converted)
                {
                    (hash, epub) = await ConvertAsync(file, source.RelativePath, ct);
                }
                else
                {
                    hash = await SHA256.HashDataAsync(file, ct);
                    file.Position = 0;
                }

                EpubPackage package;
                ZipArchive archive;
                try
                {
                    archive = new ZipArchive(epub, ZipArchiveMode.Read, leaveOpen: true);
                    package = EpubReader.Read(archive);
                }
                catch (InvalidDataException)
                {
                    throw new ServiceUnavailableException("reading_invalid_file");
                }

                using (archive)
                {
                    var save = new ReadingDocumentSaveData(source.StoredFileId, source.SizeBytes, source.ModifiedAt, hash, ReadingHtml.AnalyzerVersion, ReadingFormatType.Epub, package.Title,
                        package.Language, "{\"Sections\":[],\"Toc\":[]}", 0);
                    var state = await context.ExecuteBatchAsync((ctx, token) => ctx.CreateReadingDocumentAsync(save, token), ct);
                    if (!state.IsCached)
                    {
                        await PrepareAsync(context, state.DocumentId, archive, package, converted, ct);
                    }
                    else if (converted && !_files.IsComplete(state.DocumentId))
                    {
                        // The text is kept; the copy's pictures are made again.
                        await WritePicturesAsync(state.DocumentId, archive, package, ct);
                        await TrimAsync(context, ct);
                    }

                    return state.DocumentId;
                }
            }
        }

        // A format the reader does not read opens through its reading copy, an EPUB made in memory; the file itself stays as it is. Its hash is the file's.
        private static async Task<(byte[] Hash, Stream Epub)> ConvertAsync(Stream file, string relativePath, CancellationToken ct)
        {
            if (file.CanSeek && file.Length > BookConversion.MaximumSourceBytes)
            {
                throw new ServiceUnavailableException("reading_invalid_file");
            }

            // Read once into an array of its size where the length is known.
            byte[] bytes;
            if (file.CanSeek)
            {
                bytes = new byte[file.Length];
                await file.ReadExactlyAsync(bytes, ct);
            }
            else
            {
                using var copy = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = await file.ReadAsync(buffer, ct)) > 0)
                {
                    if (copy.Length + read > BookConversion.MaximumSourceBytes)
                    {
                        throw new ServiceUnavailableException("reading_invalid_file");
                    }

                    copy.Write(buffer, 0, read);
                }

                bytes = copy.ToArray();
            }

            try
            {
                return (SHA256.HashData(bytes), new MemoryStream(BookConversion.ToEpub(relativePath, bytes)));
            }
            catch (BookProtectedException)
            {
                throw new ServiceUnavailableException("reading_drm");
            }
            catch (BookCompressionException)
            {
                throw new ServiceUnavailableException("reading_kindle_compression");
            }
            catch (InvalidDataException)
            {
                throw new ServiceUnavailableException("reading_invalid_file");
            }
        }

        private async Task PrepareAsync(LogicContext context, long documentId, ZipArchive archive, EpubPackage package, bool converted, CancellationToken ct)
        {
            await ClearAsync(context, documentId, ct);

            // Section by section in bounded batches.
            var indexes = package.Spine.Select((item, index) => (item.Path, index)).ToDictionary(pair => pair.Path, pair => pair.index, StringComparer.Ordinal);
            var sections = new List<ReadingStructureSection>();
            var writer = new ResourceWriter(context, ct);
            var sizes = new Dictionary<string, (int Width, int Height)?>(StringComparer.Ordinal);
            (int Width, int Height)? SizeOf(string path)
            {
                if (!sizes.TryGetValue(path, out var size))
                {
                    sizes[path] = size = archive.GetEntry(path) is { } entry ? ImageSize.Read(EpubReader.ReadPrefix(entry, 256 * 1024)) : null;
                }

                return size;
            }

            var guessText = new List<string>();
            for (var index = 0; index < package.Spine.Count; index++)
            {
                var item = package.Spine[index];
                var prepared = ReadingHtml.Prepare(EpubReader.ReadText(archive.GetEntry(item.Path)!), item.Path, documentId, indexes, package.Manifest, SizeOf);
                var title = package.Toc.FirstOrDefault(entry => entry.Path == item.Path)?.Title;
                sections.Add(new ReadingStructureSection(item.Path, title, prepared.Characters, prepared.Blocks));
                await writer.AddAsync(new ReadingResourceData(documentId, SectionPath(index), "text/html; charset=utf-8", Encoding.UTF8.GetBytes(prepared.Html)));
                if (package.Language is null && guessText.Sum(text => text.Length) < 20_000)
                {
                    guessText.Add(Tags().Replace(prepared.Html, " "));
                }
            }

            await writer.FlushAsync();
            if (converted)
            {
                await WritePicturesAsync(documentId, archive, package, ct);
            }

            var toc = package.Toc.Where(entry => indexes.ContainsKey(entry.Path)).Select(entry => new ReadingStructureEntry(entry.Title, indexes[entry.Path], entry.Level, entry.Anchor)).ToArray();
            var structure = JsonSerializer.Serialize(new ReadingStructure([.. sections], toc));
            // An EPUB without its language tag: the text of its first sections tells.
            var language = package.Language ?? ReadingLanguage.Guess(string.Concat(guessText));
            await CompleteAsync(context, new ReadingDocumentCompleteData(documentId, package.Title, language, structure, sections.Sum(section => section.Characters)), ct);
        }

        // The pictures of a book read through a copy made on opening, which its file cannot answer.
        private async Task WritePicturesAsync(long documentId, ZipArchive archive, EpubPackage package, CancellationToken ct)
        {
            foreach (var (path, mediaType) in package.Manifest.Where(item => item.Value.StartsWith("image/", StringComparison.Ordinal)))
            {
                if (archive.GetEntry(path) is { } entry)
                {
                    await _files.WriteAsync(documentId, path, mediaType, EpubReader.ReadBytes(entry), ct);
                }
            }

            _files.Complete(documentId);
        }

        // A PDF reads like an EPUB once its text is reflowed, with the pages that do not reflow as the original's; one without text stays for the original pages. The document says whether it is fit to open by itself.
        private async Task<long> OpenPdfAsync(LogicContext context, ReadingSourceData source, Stream file, CancellationToken ct)
        {
            var hash = await SHA256.HashDataAsync(file, ct);
            file.Position = 0;

            // Only a PDF with text becomes a document; a scan waits for recognition where the Ocr module reads it, a page at a time.
            var recognized = await context.ExecuteBatchAsync((ctx, token) => ReadRecognizedAsync(ctx, new ReadingRecognitionData(source.StoredFileId, source.SizeBytes, source.ModifiedAt), token), ct);

            PdfBookContent book;
            try
            {
                book = await ReadPdfAsync(file, recognized, ct);
            }
            catch (PdfWithoutTextException)
            {
                var recognition = new ReadingRecognitionData(source.StoredFileId, source.SizeBytes, source.ModifiedAt);
                throw new ServiceUnavailableException(await context.ExecuteBatchAsync((ctx, token) => ctx.RequestReadingRecognitionAsync(recognition, token), ct)
                    ? "reading_pdf_recognizing" : "reading_pdf_without_text");
            }
            catch (InvalidDataException)
            {
                throw new ServiceUnavailableException("reading_invalid_file");
            }

            if (book.Reading.Reason == "no_text")
            {
                throw new ServiceUnavailableException("reading_pdf_without_text");
            }

            // Scanned pages with their own recognition layer read meanwhile from it; the recognizer reads them anew and finds their pictures where the Ocr module is on.
            if (book.WantsRecognition)
            {
                var recognition = new ReadingRecognitionData(source.StoredFileId, source.SizeBytes, source.ModifiedAt);
                await context.ExecuteBatchAsync((ctx, token) => ctx.RequestReadingRecognitionAsync(recognition, token), ct);
            }

            var save = new ReadingDocumentSaveData(source.StoredFileId, source.SizeBytes, source.ModifiedAt, hash, PdfBook.AnalyzerVersion, ReadingFormatType.Pdf, null, null,
                "{\"Sections\":[],\"Toc\":[]}", 0);
            var state = await context.ExecuteBatchAsync((ctx, token) => ctx.CreateReadingDocumentAsync(save, token), ct);
            if (state.IsCached)
            {
                // The text is kept; the pictures just drawn are kept on disk again.
                if (!_files.IsComplete(state.DocumentId))
                {
                    await WritePdfFilesAsync(state.DocumentId, book, ct);
                    await TrimAsync(context, ct);
                }

                return state.DocumentId;
            }

            await ClearAsync(context, state.DocumentId, ct);

            // Each chapter becomes one section of plain paragraphs, pictures and figures, prepared like any book text; the pictures go to disk.
            var prepared = PdfBookHtml.Prepare(book, state.DocumentId);
            await WritePdfFilesAsync(state.DocumentId, book, ct);
            var writer = new ResourceWriter(context, ct);
            for (var index = 0; index < prepared.Sections.Count; index++)
            {
                await writer.AddAsync(new ReadingResourceData(state.DocumentId, SectionPath(index), "text/html; charset=utf-8", Encoding.UTF8.GetBytes(prepared.Sections[index].Html)));
            }

            for (var index = 0; index < prepared.SmartPages.Count; index++)
            {
                await writer.AddAsync(new ReadingResourceData(state.DocumentId, PdfBookHtml.SmartPagePath(index), "text/html; charset=utf-8", Encoding.UTF8.GetBytes(prepared.SmartPages[index].Html)));
            }

            await writer.FlushAsync();
            var structure = prepared.Sections.Select(section => section.Structure).ToArray();
            var recommended = book.Reading.IsReadable;
            // A PDF names no language: its text tells, so the server's translation knows the book's own.
            var language = ReadingLanguage.Guess(string.Join(' ', book.Chapters.SelectMany(chapter => chapter.Blocks).Select(block => block.Text).Where(text => text is not null).Take(200)));
            await CompleteAsync(context, new ReadingDocumentCompleteData(state.DocumentId, null, language,
                JsonSerializer.Serialize(new ReadingStructure(structure, [.. prepared.Toc], recommended, recommended ? null : book.Reading.Reason, [.. prepared.OriginalPages],
                    [.. prepared.SmartPages.Select(page => page.Structure)])),
                structure.Sum(section => section.Characters)), ct);
            return state.DocumentId;
        }

        // Pictures the recognizer found on scanned pages, and pages that show best whole, are cut from the pages PDFium draws.
        private static async Task<PdfBookContent> ReadPdfAsync(Stream file, IReadOnlyDictionary<int, RecognizedPage> recognized, CancellationToken ct)
        {
            PdfRendering? rendering = null;
            if (recognized.Values.Any(page => page.Reason == RecognizedPage.Recognized) && file.Length <= PdfBook.MaximumRenderBytes)
            {
                using var copy = new MemoryStream();
                await file.CopyToAsync(copy, ct);
                file.Position = 0;
                try
                {
                    rendering = PdfRendering.Open(copy.ToArray());
                }
                catch (InvalidDataException)
                {
                    // Without a drawing the scan's pictures stay with the original pages.
                }
            }

            using (rendering)
            {
                return PdfBook.Read(file, recognized, rendering);
            }
        }

        private async Task WritePdfFilesAsync(long documentId, PdfBookContent book, CancellationToken ct)
        {
            foreach (var image in book.Images)
            {
                await _files.WriteAsync(documentId, image.Path, image.MediaType, image.Bytes, ct);
            }

            foreach (var page in book.SmartPages)
            {
                await _files.WriteAsync(documentId, PdfBookHtml.SmartImagePath(page.Number), "image/jpeg", page.Image, ct);
            }

            _files.Complete(documentId);
        }

        // What the recognizer has read of this version of the PDF so far, by page.
        private static async Task<IReadOnlyDictionary<int, RecognizedPage>> ReadRecognizedAsync(LogicContext ctx, ReadingRecognitionData source, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    page."StoredFileId",
                    page."PageNumber",
                    page."Reason",
                    page."Quality",
                    page."Lines"::text AS "Lines"
                FROM
                    "ReadingRecognizedPages" AS page
                    JOIN "ReadingRecognitions" AS recognition ON recognition."StoredFileId" = page."StoredFileId"
                        AND recognition."SourceSizeBytes" = @SizeBytes
                        AND recognition."SourceModifiedAt" = @ModifiedAt
                WHERE
                    page."StoredFileId" = @StoredFileId;
                """;
            sql.Parameters.Add(source);
            var pages = await ctx.ReadSql.ReadListAsync<ReadingRecognizedPageData>(sql, ct);
            return pages.ToDictionary(page => page.PageNumber, page => new RecognizedPage(page.Reason, (double)page.Quality, RecognizedPage.Parse(page.Lines)));
        }

        private async Task<long> OpenComicAsync(LogicContext context, ReadingSourceData source, Stream file, CancellationToken ct)
        {
            var hash = await SHA256.HashDataAsync(file, ct);
            file.Position = 0;
            var save = new ReadingDocumentSaveData(source.StoredFileId, source.SizeBytes, source.ModifiedAt, hash, ComicPages.AnalyzerVersion, ReadingFormatType.Comic, null, null,
                JsonSerializer.Serialize(new ReadingStructure([], [])), 0);
            var state = await context.ExecuteBatchAsync((ctx, token) => ctx.CreateReadingDocumentAsync(save, token), ct);
            if (state.IsCached && (!KeepsFiles(source.RelativePath) || _files.IsComplete(state.DocumentId)))
            {
                return state.DocumentId;
            }

            // The pages are counted (and for an unpacked archive kept on disk) the same way each time, so a page's number stays the same.
            await ReadComicAsync(source.RelativePath, file, (pages, keepsPages) => PrepareComicAsync(state.IsCached ? null : context, state.DocumentId, pages, keepsPages, ct), ct);
            if (state.IsCached)
            {
                await TrimAsync(context, ct);
            }

            return state.DocumentId;
        }

        // CBZ is read in place; CBR, CB7 and mislabelled archives are unpacked once by the approved 7-Zip into a private folder, whose pages are kept on disk.
        private async Task ReadComicAsync(string relativePath, Stream file, Func<ComicPage[], bool, Task> use, CancellationToken ct)
        {
            if (ComicPages.IsZip(relativePath))
            {
                ZipArchive? archive = null;
                try
                {
                    archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
                }
                catch (InvalidDataException)
                {
                    file.Position = 0;
                }

                if (archive is not null)
                {
                    using (archive)
                    {
                        await use([.. archive.Entries.Where(entry => ComicPages.IsPage(entry.FullName)).Select(entry => new ComicPage(entry.FullName, entry.Length, entry.Open))], false);
                    }

                    return;
                }
            }

            if (!_archives.CanExtract)
            {
                throw new ServiceUnavailableException("reading_unsupported");
            }

            // 7-Zip reads a file on this host; an archive on a direct share is copied aside first.
            var folder = Directory.CreateTempSubdirectory("jularr-comic-").FullName;
            var copy = file is FileStream ? null : folder + Path.GetExtension(relativePath);
            try
            {
                if (copy is not null)
                {
                    await using var target = File.Create(copy);
                    await file.CopyToAsync(target, ct);
                }

                if (!await _archives.ExtractAsync(copy ?? ((FileStream)file).Name, folder, null, ct))
                {
                    throw new ServiceUnavailableException("reading_invalid_file");
                }

                // Links never lead out of the unpacked folder.
                var pages = new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                    .Where(info => ComicPages.IsPage(Path.GetRelativePath(folder, info.FullName)))
                    .Select(info => new ComicPage(Path.GetRelativePath(folder, info.FullName), info.Length, info.OpenRead));
                await use([.. pages], true);
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, true);
                    if (copy is not null) File.Delete(copy);
                }
                catch (IOException)
                {
                    // A busy temporary folder must not hide the outcome.
                }
            }
        }

        // Without a context only the pages of a prepared comic are kept on disk again.
        private async Task PrepareComicAsync(LogicContext? context, long documentId, ComicPage[] candidates, bool keepsPages, CancellationToken ct)
        {
            if (context is not null)
            {
                await ClearAsync(context, documentId, ct);
            }

            // Pages in natural order; each one is an image by its bytes, not its name; unreadable entries are skipped.
            var pages = new List<ReadingStructureSection>();
            long total = 0;
            foreach (var candidate in candidates.OrderBy(page => ComicPages.SortKey(page.Path), StringComparer.Ordinal).ThenBy(page => page.Path, StringComparer.Ordinal))
            {
                if (pages.Count == ComicPages.MaximumPages)
                {
                    break;
                }

                if (candidate.Length is <= 0 or > ComicPages.MaximumPageBytes)
                {
                    continue;
                }

                total += candidate.Length;
                if (total > ComicPages.MaximumVolumeBytes)
                {
                    throw new ServiceUnavailableException("reading_invalid_file");
                }

                byte[] bytes;
                string? contentType;
                try
                {
                    await using var stream = candidate.Open();
                    var header = new byte[16];
                    var read = await stream.ReadAtLeastAsync(header, header.Length, false, ct);
                    contentType = ComicPages.ContentType(header.AsSpan(0, read));
                    if (contentType is null)
                    {
                        continue;
                    }

                    bytes = new byte[candidate.Length];
                    header.AsSpan(0, read).CopyTo(bytes);
                    await stream.ReadExactlyAsync(bytes.AsMemory(read), ct);
                }
                catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or EndOfStreamException or IOException)
                {
                    continue;
                }

                if (keepsPages)
                {
                    await _files.WriteAsync(documentId, ComicPages.PagePath(pages.Count), contentType, bytes, ct);
                }

                pages.Add(new ReadingStructureSection(candidate.Path, null, 0, 1));
            }

            if (pages.Count == 0)
            {
                throw new ServiceUnavailableException("reading_invalid_file");
            }

            if (keepsPages)
            {
                _files.Complete(documentId);
            }

            if (context is not null)
            {
                await CompleteAsync(context, new ReadingDocumentCompleteData(documentId, null, null, JsonSerializer.Serialize(new ReadingStructure([.. pages], [])), 0), ct);
            }
        }

        private async Task<Stream> OpenFileAsync(LogicContext context, long storageMountId, string rootRelativePath, string relativePath, CancellationToken ct)
        {
            try
            {
                var mount = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadStorageMountAsync(storageMountId, token), ct);
                return await _storage.Open(mount).OpenReadAsync(StoragePath.Combine(rootRelativePath, relativePath), ct);
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                // Offline storage is not a missing book.
                throw new ServiceUnavailableException("reading_unavailable");
            }
        }

        // A DjVu book opens as its pages, counted now and drawn by DjVuLibre when they are first shown.
        private async Task<long> OpenDjvuAsync(LogicContext context, ReadingSourceData source, Stream file, CancellationToken ct)
        {
            if (!_djvu.IsAvailable)
            {
                throw new ServiceUnavailableException("reading_djvu_unavailable");
            }

            var hash = await SHA256.HashDataAsync(file, ct);
            file.Position = 0;
            var save = new ReadingDocumentSaveData(source.StoredFileId, source.SizeBytes, source.ModifiedAt, hash, ComicPages.AnalyzerVersion, ReadingFormatType.Comic, null, null,
                JsonSerializer.Serialize(new ReadingStructure([], [])), 0);
            var state = await context.ExecuteBatchAsync((ctx, token) => ctx.CreateReadingDocumentAsync(save, token), ct);
            if (state.IsCached)
            {
                return state.DocumentId;
            }

            await ClearAsync(context, state.DocumentId, ct);
            var count = await WithDjvuAsync(file, path => _djvu.CountPagesAsync(path, ct), ct);
            var pages = Enumerable.Range(1, Math.Min(count, DjvuTools.MaximumPages)).Select(number => new ReadingStructureSection("page " + number.ToString(CultureInfo.InvariantCulture), null, 0, 1));
            await CompleteAsync(context, new ReadingDocumentCompleteData(state.DocumentId, null, null, JsonSerializer.Serialize(new ReadingStructure([.. pages], [], rendersPages: true)), 0), ct);
            return state.DocumentId;
        }

        // A page of a DjVu book from the disk, else drawn now with the next pages not drawn yet; only the asked page must succeed.
        public async Task<ReadingResourceV1> RenderPageAsync(LogicContext context, ReadingPageSourceData source, int index, CancellationToken ct)
        {
            if (_files.Find(source.DocumentId, ComicPages.PagePath(index)) is { } kept)
            {
                return new ReadingResourceV1(await File.ReadAllBytesAsync(kept.FullPath, ct), kept.MediaType);
            }

            if (!source.IsCurrent)
            {
                throw new ServiceUnavailableException("reading_unavailable");
            }

            if (!_djvu.IsAvailable)
            {
                throw new ServiceUnavailableException("reading_djvu_unavailable");
            }

            await DjvuGate.WaitAsync(ct);
            try
            {
                // Another request may have drawn these pages while this one waited.
                var paths = Enumerable.Range(index, Math.Min(PagesAhead, source.SectionCount - index)).Select(ComicPages.PagePath).ToArray();
                if (_files.Find(source.DocumentId, paths[0]) is { } ready)
                {
                    return new ReadingResourceV1(await File.ReadAllBytesAsync(ready.FullPath, ct), ready.MediaType);
                }

                var missing = paths.Select((path, offset) => (Path: path, Number: index + offset + 1)).Where(page => _files.Find(source.DocumentId, page.Path) is null).ToArray();
                var file = await OpenFileAsync(context, source.StorageMountId, source.RootRelativePath, source.RelativePath, ct);
                DjvuPageImage?[] drawn;
                await using (file)
                {
                    drawn = await WithDjvuAsync(file, async path =>
                    {
                        var sizes = await _djvu.ReadSizesAsync(path, [.. missing.Select(page => page.Number)], ct);
                        var images = new DjvuPageImage?[sizes.Count];
                        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, PagesAhead), CancellationToken = ct };
                        await Parallel.ForEachAsync(Enumerable.Range(0, sizes.Count), parallel, async (offset, token) =>
                        {
                            try
                            {
                                images[offset] = await _djvu.RenderAsync(path, sizes[offset], token);
                            }
                            catch (Exception exception) when (offset > 0 && exception is InvalidDataException or TimeoutException)
                            {
                                // A later page is tried again when it is asked for.
                            }
                        });
                        return images;
                    }, ct);
                }

                for (var offset = 0; offset < drawn.Length; offset++)
                {
                    if (drawn[offset] is { } image)
                    {
                        await _files.WriteAsync(source.DocumentId, missing[offset].Path, image.ContentType, image.Bytes, ct);
                    }
                }

                _files.Trim(source.CacheBudgetBytes);
                return new ReadingResourceV1(drawn[0]!.Bytes, drawn[0]!.ContentType);
            }
            finally
            {
                DjvuGate.Release();
            }
        }

        // DjVuLibre reads a file on this host: a book on a direct share is copied aside into a private folder first. A file it cannot read is an invalid book.
        private static async Task<T> WithDjvuAsync<T>(Stream file, Func<string, Task<T>> use, CancellationToken ct)
        {
            var folder = file is FileStream ? null : Directory.CreateTempSubdirectory("jularr-djvu-").FullName;
            try
            {
                var path = folder is null ? Path.GetFullPath(((FileStream)file).Name) : Path.Combine(folder, "book.djvu");
                if (folder is not null)
                {
                    await using var target = File.Create(path);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await file.ReadAsync(buffer, ct)) > 0)
                    {
                        if (target.Length + read > MaximumDjvuBytes)
                        {
                            throw new ServiceUnavailableException("reading_invalid_file");
                        }

                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                }

                return await use(path);
            }
            catch (DjvuUnavailableException)
            {
                throw new ServiceUnavailableException("reading_djvu_unavailable");
            }
            catch (Exception exception) when (exception is InvalidDataException or TimeoutException)
            {
                throw new ServiceUnavailableException("reading_invalid_file");
            }
            finally
            {
                try
                {
                    if (folder is not null) Directory.Delete(folder, true);
                }
                catch (IOException)
                {
                    // A busy temporary folder must not hide the outcome.
                }
            }
        }

        // Documents leaving the database cache take their files along; the disk stays within the same budget.
        private async Task CompleteAsync(LogicContext context, ReadingDocumentCompleteData complete, CancellationToken ct)
        {
            var (evicted, budget) = await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.CompleteReadingDocumentAsync(complete, token);
                var budget = await ReadBudgetAsync(ctx, token);
                var evicted = await ctx.EvictReadingCacheAsync(budget, token);

                // The prepared book and those that left the cache count their translation again, together and in Id order, so the counts' locks are
                // taken in the order the job's full count takes them.
                foreach (var books in evicted.Append(complete.DocumentId).Distinct().Order().Chunk(500))
                {
                    await ctx.RecountBookTranslationProgressAsync(new BookTranslationRecountData(books, null), token);
                }

                return (evicted, budget.CacheBudgetBytes);
            }, ct);
            _files.Delete(evicted);
            _files.Trim(budget);
        }

        // An interrupted or outdated preparation starts over, on disk as well.
        private async Task ClearAsync(LogicContext context, long documentId, CancellationToken ct)
        {
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.ClearReadingResourcesAsync(new ReadingDocumentIdData(documentId), token);
                return true;
            }, ct);
            _files.Delete([documentId]);
        }

        private static async Task<long> TouchAsync(LogicContext context, long documentId, CancellationToken ct)
        {
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.TouchReadingDocumentAsync(new ReadingDocumentIdData(documentId), token);
                return true;
            }, ct);
            return documentId;
        }

        private async Task TrimAsync(LogicContext context, CancellationToken ct) =>
            _files.Trim((await context.ExecuteBatchAsync((ctx, token) => ReadBudgetAsync(ctx, token), ct)).CacheBudgetBytes);

        // A document's file and whether it is still the version the document was made of, for a reader that may open it; the page's entry where it names a comic page.
        public static Task<ReadingFileSourceData?> ReadFileSourceAsync(LogicContext ctx, long documentId, long profileId, string? path, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    document."Id" AS "DocumentId",
                    document."ReadingFormatTypeId",
                    file."Id" AS "StoredFileId",
                    file."RelativePath",
                    file."SizeBytes",
                    file."ModifiedAt",
                    root."RelativePath" AS "RootRelativePath",
                    mount."Id" AS "StorageMountId",
                    (file."MissingSince" IS NULL
                        AND file."SizeBytes" = document."SourceSizeBytes"
                        AND file."ModifiedAt" = document."SourceModifiedAt") AS "IsCurrent",
                    CASE WHEN document."ReadingFormatTypeId" = 4 AND @PageIndex >= 0 THEN document."Structure" -> 'Sections' -> @PageIndex ->> 'Path' END AS "PageEntry",
                    coalesce((SELECT settings."ReadingCacheBytes" FROM "InstanceSettings" AS settings), 2147483648) AS "CacheBudgetBytes"
                FROM
                    "ReadingDocuments" AS document
                    JOIN "StoredFiles" AS file ON file."Id" = document."StoredFileId"
                    JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                WHERE
                    document."Id" = @DocumentId
                    AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId);
                """;
            var page = path is not null && path.StartsWith(":page/", StringComparison.Ordinal) && int.TryParse(path.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number < ComicPages.MaximumPages ? number : -1;
            sql.Parameters.Add(new ReadingFileLookupData(documentId, profileId, page));
            return ctx.ReadSql.ReadOptionalAsync<ReadingFileSourceData>(sql, ct);
        }

        // A picture or page a reader shows (in a read-only service): kept on disk, else read from an EPUB or CBZ itself and kept, else the book's pictures drawn again.
        // None when the book has no such picture.
        public async Task<ReadingFile?> ReadFileAsync(LogicContext context, ReadingFileSourceData source, string path, CancellationToken ct) =>
            (await ReadFilesAsync(context, source, [path], ct))[0];

        // Several of them in the order asked. Before the book's own file is reached, what it needs from the database is read and the read transaction ends,
        // so the caller reads nothing from the database afterwards.
        public async Task<IReadOnlyList<ReadingFile?>> ReadFilesAsync(LogicContext context, ReadingFileSourceData source, IReadOnlyList<string> paths, CancellationToken ct)
        {
            var files = new ReadingFile?[paths.Count];
            StoredBook? book = null;
            for (var index = 0; index < paths.Count; index++)
            {
                if (_files.Find(source.DocumentId, paths[index]) is { } kept)
                {
                    files[index] = kept;
                    continue;
                }

                // A complete set has no such picture; a DjVu page is drawn through the reader's page handler only.
                if (_files.IsComplete(source.DocumentId) || IsDjvu(source.RelativePath))
                {
                    continue;
                }

                if (!source.IsCurrent)
                {
                    throw new ServiceUnavailableException("reading_unavailable");
                }

                if (book is null)
                {
                    var mount = await context.ReadStorageMountAsync(source.StorageMountId, ct);
                    var recognized = source.ReadingFormatTypeId == ReadingFormatType.Pdf
                        ? await ReadRecognizedAsync(context, new ReadingRecognitionData(source.StoredFileId, source.SizeBytes, source.ModifiedAt), ct) : null;
                    await context.EndReadAsync(ct);
                    book = new StoredBook(mount, recognized);
                }

                files[index] = await ReadStoredFileAsync(book, source, paths[index], ct);
            }

            return files;
        }

        // The book's file as its storage holds it, once the database has given what it needs: its mount and, for a PDF, the recognized pages.
        private sealed record StoredBook(StorageMountData Mount, IReadOnlyDictionary<int, RecognizedPage>? Recognized);

        private async Task<ReadingFile?> ReadStoredFileAsync(StoredBook book, ReadingFileSourceData source, string path, CancellationToken ct)
        {
            Stream file;
            try
            {
                file = await _storage.Open(book.Mount).OpenReadAsync(StoragePath.Combine(source.RootRelativePath, source.RelativePath), ct);
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                throw new ServiceUnavailableException("reading_unavailable");
            }

            await using (file)
            {
                // An EPUB's picture and a CBZ's page, read from the archive itself; a mislabelled CBZ goes through 7-Zip below.
                var entryPath = source.ReadingFormatTypeId == ReadingFormatType.Comic ? source.PageEntry : path;
                if (!KeepsFiles(source.RelativePath))
                {
                    if (entryPath is null)
                    {
                        return null;
                    }

                    try
                    {
                        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
                        if (archive.GetEntry(entryPath) is not { Length: > 0 and <= EpubReader.MaximumEntryBytes } entry)
                        {
                            return null;
                        }

                        var bytes = EpubReader.ReadBytes(entry);
                        var mediaType = ComicPages.ContentType(bytes.AsSpan(0, Math.Min(16, bytes.Length)))
                            ?? (source.ReadingFormatTypeId == ReadingFormatType.Epub && entryPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml" : null);
                        if (mediaType is null)
                        {
                            return null;
                        }

                        var written = await _files.WriteAsync(source.DocumentId, path, mediaType, bytes, ct);
                        _files.Trim(source.CacheBudgetBytes);
                        return written;
                    }
                    catch (InvalidDataException) when (source.ReadingFormatTypeId == ReadingFormatType.Comic)
                    {
                        file.Position = 0;
                    }
                    catch (InvalidDataException)
                    {
                        return null;
                    }
                }

                // Pictures the server makes are drawn again for the whole book, one book at a time; a request that waited finds them kept.
                await RedrawGate.WaitAsync(ct);
                try
                {
                    if (_files.IsComplete(source.DocumentId))
                    {
                        return _files.Find(source.DocumentId, path);
                    }

                    if (source.ReadingFormatTypeId == ReadingFormatType.Pdf)
                    {
                        try
                        {
                            await WritePdfFilesAsync(source.DocumentId, await ReadPdfAsync(file, book.Recognized!, ct), ct);
                        }
                        catch (Exception exception) when (exception is PdfWithoutTextException or InvalidDataException)
                        {
                            throw new ServiceUnavailableException("reading_invalid_file");
                        }
                    }
                    else if (source.ReadingFormatTypeId == ReadingFormatType.Comic)
                    {
                        await ReadComicAsync(source.RelativePath, file, (pages, _) => PrepareComicAsync(null, source.DocumentId, pages, true, ct), ct);
                    }
                    else if (BookConversion.IsConverted(source.RelativePath))
                    {
                        var (_, epub) = await ConvertAsync(file, source.RelativePath, ct);
                        try
                        {
                            using var archive = new ZipArchive(epub, ZipArchiveMode.Read);
                            await WritePicturesAsync(source.DocumentId, archive, EpubReader.Read(archive), ct);
                        }
                        catch (InvalidDataException)
                        {
                            throw new ServiceUnavailableException("reading_invalid_file");
                        }
                    }
                }
                finally
                {
                    RedrawGate.Release();
                }

                _files.Trim(source.CacheBudgetBytes);
                return _files.Find(source.DocumentId, path);
            }
        }

        private sealed record ComicPage(string Path, long Length, Func<Stream> Open);

        // Sections reach the database in bounded batches.
        private sealed class ResourceWriter(LogicContext context, CancellationToken ct)
        {
            private readonly List<ReadingResourceData> _batch = [];
            private long _bytes;

            public async Task AddAsync(ReadingResourceData resource)
            {
                _batch.Add(resource);
                _bytes += resource.Bytes.Length;
                if (_bytes >= BatchBytes)
                {
                    await FlushAsync();
                }
            }

            public async Task FlushAsync()
            {
                if (_batch.Count == 0)
                {
                    return;
                }

                var rows = _batch.ToArray();
                _batch.Clear();
                _bytes = 0;
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    foreach (var row in rows)
                    {
                        await ctx.SaveReadingResourceAsync(row, token);
                    }

                    return true;
                }, ct);
            }
        }

        private async Task<ReadingCacheBudgetData> ReadBudgetAsync(LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."ReadingCacheBytes" AS "CacheBudgetBytes",
                    settings."ReadingCacheWorks" AS "CacheBudgetWorks",
                    now() - interval '14 days' AS "ProtectedSince"
                FROM
                    "InstanceSettings" AS settings;
                """;
            return await ctx.ReadSql.ReadOptionalAsync<ReadingCacheBudgetData>(sql, ct)
                ?? new ReadingCacheBudgetData(2L * 1024 * 1024 * 1024, 100, _time.GetUtcNow().UtcDateTime - ReadingLogic.ProtectedFor);
        }
    }
}
