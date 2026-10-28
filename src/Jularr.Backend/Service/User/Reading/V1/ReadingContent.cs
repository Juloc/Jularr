using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Pdf;
using Jularr.Integrations.Logic.Storage;
using Jularr.Integrations.Logic.Usenet;

namespace Jularr.Backend.Service.User.Reading.V1
{
    // Prepares books and comics from storage into the shared reading cache; I/O stays outside transactions.
    internal sealed class ReadingContent
    {
        private const long BatchBytes = 8L * 1024 * 1024;
        private readonly StorageAccess _storage;
        private readonly ArchiveTools _archives;
        private readonly TimeProvider _time;

        public ReadingContent(StorageAccess storage, ArchiveTools archives, TimeProvider time)
        {
            _storage = storage;
            _archives = archives;
            _time = time;
        }

        public static string SectionPath(int index) => ":section/" + index.ToString("D5", CultureInfo.InvariantCulture);

        // A scanned PDF's pages the recognizer has read go into the text.
        public async Task<long> OpenAsync(LogicContext context, ReadingSourceData source, CancellationToken ct)
        {
            // A complete cache needs no storage.
            if (source is { DocumentId: { } cached, IsCached: true })
            {
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    await ctx.TouchReadingDocumentAsync(new ReadingDocumentIdData(cached), token);
                    return true;
                }, ct);
                return cached;
            }

            if (source.IsMissing)
            {
                throw new ServiceUnavailableException("reading_unavailable");
            }

            Stream file;
            try
            {
                var mount = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadStorageMountAsync(source.StorageMountId, token), ct);
                file = await _storage.Open(mount).OpenReadAsync(StoragePath.Combine(source.RootRelativePath, source.RelativePath), ct);
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                // Offline storage is not a missing book.
                throw new ServiceUnavailableException("reading_unavailable");
            }

            await using (file)
            {
                if (ComicPages.IsComic(source.RelativePath))
                {
                    return await OpenComicAsync(context, source, file, ct);
                }

                if (source.RelativePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    return await OpenPdfAsync(context, source, file, ct);
                }

                var hash = await SHA256.HashDataAsync(file, ct);
                file.Position = 0;
                EpubPackage package;
                ZipArchive archive;
                try
                {
                    archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
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
                        await PrepareAsync(context, state.DocumentId, archive, package, ct);
                    }

                    return state.DocumentId;
                }
            }
        }

        private async Task PrepareAsync(LogicContext context, long documentId, ZipArchive archive, EpubPackage package, CancellationToken ct)
        {
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.ClearReadingResourcesAsync(new ReadingDocumentIdData(documentId), token);
                return true;
            }, ct);

            // Section by section in bounded batches.
            var indexes = package.Spine.Select((item, index) => (item.Path, index)).ToDictionary(pair => pair.Path, pair => pair.index, StringComparer.Ordinal);
            var sections = new List<ReadingStructureSection>();
            var images = new HashSet<string>(StringComparer.Ordinal);
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

            for (var index = 0; index < package.Spine.Count; index++)
            {
                var item = package.Spine[index];
                var prepared = ReadingHtml.Prepare(EpubReader.ReadText(archive.GetEntry(item.Path)!), item.Path, documentId, indexes, package.Manifest, SizeOf);
                var title = package.Toc.FirstOrDefault(entry => entry.Path == item.Path)?.Title;
                sections.Add(new ReadingStructureSection(item.Path, title, prepared.Characters, prepared.Blocks));
                await writer.AddAsync(new ReadingResourceData(documentId, SectionPath(index), "text/html; charset=utf-8", Encoding.UTF8.GetBytes(prepared.Html)));
                foreach (var image in prepared.Resources.Where(images.Add))
                {
                    if (archive.GetEntry(image) is { } entry)
                    {
                        await writer.AddAsync(new ReadingResourceData(documentId, image, package.Manifest[image], EpubReader.ReadBytes(entry)));
                    }
                }
            }

            await writer.FlushAsync();
            var toc = package.Toc.Where(entry => indexes.ContainsKey(entry.Path)).Select(entry => new ReadingStructureEntry(entry.Title, indexes[entry.Path], entry.Level, entry.Anchor)).ToArray();
            var structure = JsonSerializer.Serialize(new ReadingStructure([.. sections], toc));
            await CompleteAsync(context, new ReadingDocumentCompleteData(documentId, package.Title, package.Language, structure, sections.Sum(section => section.Characters)), ct);
        }

        // A PDF reads like an EPUB once its text is reflowed, with the pages that do not reflow as the original's; one without text stays for the original pages. The document says whether it is fit to open by itself.
        private async Task<long> OpenPdfAsync(LogicContext context, ReadingSourceData source, Stream file, CancellationToken ct)
        {
            var hash = await SHA256.HashDataAsync(file, ct);
            file.Position = 0;

            // Only a PDF with text becomes a document; a scan waits for recognition where the Ocr module reads it, a page at a time.
            var recognized = await context.ExecuteBatchAsync((ctx, token) => ReadRecognizedAsync(ctx, source, token), ct);

            // Pictures the recognizer found on scanned pages, and pages that show best whole, are cut from the pages PDFium draws.
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

            PdfBookContent book;
            try
            {
                using (rendering)
                {
                    book = PdfBook.Read(file, recognized, rendering);
                }
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
                return state.DocumentId;
            }

            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.ClearReadingResourcesAsync(new ReadingDocumentIdData(state.DocumentId), token);
                return true;
            }, ct);

            // Each chapter becomes one section of plain paragraphs, pictures and figures, prepared like any book text.
            var prepared = PdfBookHtml.Prepare(book, state.DocumentId);
            var writer = new ResourceWriter(context, ct);
            foreach (var image in book.Images)
            {
                await writer.AddAsync(new ReadingResourceData(state.DocumentId, image.Path, image.MediaType, image.Bytes));
            }

            for (var index = 0; index < prepared.Sections.Count; index++)
            {
                await writer.AddAsync(new ReadingResourceData(state.DocumentId, SectionPath(index), "text/html; charset=utf-8", Encoding.UTF8.GetBytes(prepared.Sections[index].Html)));
            }

            await writer.FlushAsync();
            var structure = prepared.Sections.Select(section => section.Structure).ToArray();
            var recommended = book.Reading.IsReadable;
            await CompleteAsync(context, new ReadingDocumentCompleteData(state.DocumentId, null, null,
                JsonSerializer.Serialize(new ReadingStructure(structure, [.. prepared.Toc], recommended, recommended ? null : book.Reading.Reason, [.. prepared.OriginalPages])),
                structure.Sum(section => section.Characters)), ct);
            return state.DocumentId;
        }

        // What the recognizer has read of this version of the PDF so far, by page.
        private static async Task<IReadOnlyDictionary<int, RecognizedPage>> ReadRecognizedAsync(LogicContext ctx, ReadingSourceData source, CancellationToken ct)
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
            sql.Parameters.Add(new ReadingRecognitionData(source.StoredFileId, source.SizeBytes, source.ModifiedAt));
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
            if (state.IsCached)
            {
                return state.DocumentId;
            }

            // CBZ is read in place; CBR, CB7 and mislabelled archives are unpacked once by the approved 7-Zip into a private folder.
            if (ComicPages.IsZip(source.RelativePath))
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
                        await PrepareComicAsync(context, state.DocumentId,
                            [.. archive.Entries.Where(entry => ComicPages.IsPage(entry.FullName)).Select(entry => new ComicPage(entry.FullName, entry.Length, entry.Open))], ct);
                    }

                    return state.DocumentId;
                }
            }

            if (!_archives.CanExtract)
            {
                throw new ServiceUnavailableException("reading_unsupported");
            }

            // 7-Zip reads a file on this host; an archive on a direct share is copied aside first.
            var folder = Directory.CreateTempSubdirectory("jularr-comic-").FullName;
            var copy = file is FileStream ? null : folder + Path.GetExtension(source.RelativePath);
            try
            {
                if (copy is not null)
                {
                    await using var target = File.Create(copy);
                    await file.CopyToAsync(target, ct);
                }

                if (!await _archives.ExtractAsync(copy ?? ((FileStream)file).Name, folder, ct))
                {
                    throw new ServiceUnavailableException("reading_invalid_file");
                }

                // Links never lead out of the unpacked folder.
                var pages = new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                    .Where(info => ComicPages.IsPage(Path.GetRelativePath(folder, info.FullName)))
                    .Select(info => new ComicPage(Path.GetRelativePath(folder, info.FullName), info.Length, info.OpenRead));
                await PrepareComicAsync(context, state.DocumentId, [.. pages], ct);
                return state.DocumentId;
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

        private async Task PrepareComicAsync(LogicContext context, long documentId, ComicPage[] candidates, CancellationToken ct)
        {
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.ClearReadingResourcesAsync(new ReadingDocumentIdData(documentId), token);
                return true;
            }, ct);

            // Pages in natural order; each one is an image by its bytes, not its name; unreadable entries are skipped.
            var pages = new List<ReadingStructureSection>();
            var writer = new ResourceWriter(context, ct);
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

                await writer.AddAsync(new ReadingResourceData(documentId, ComicPages.PagePath(pages.Count), contentType, bytes));
                pages.Add(new ReadingStructureSection(candidate.Path, null, 0, 1));
            }

            if (pages.Count == 0)
            {
                throw new ServiceUnavailableException("reading_invalid_file");
            }

            await writer.FlushAsync();
            await CompleteAsync(context, new ReadingDocumentCompleteData(documentId, null, null, JsonSerializer.Serialize(new ReadingStructure([.. pages], [])), 0), ct);
        }

        private async Task CompleteAsync(LogicContext context, ReadingDocumentCompleteData complete, CancellationToken ct)
        {
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.CompleteReadingDocumentAsync(complete, token);
                await ctx.EvictReadingCacheAsync(await ReadBudgetAsync(ctx, token), token);
                return true;
            }, ct);
        }

        private sealed record ComicPage(string Path, long Length, Func<Stream> Open);

        // Resources reach the cache in bounded batches.
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
