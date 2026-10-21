using System.IO.Compression;
using System.Security.Cryptography;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.User.Reading.V1
{
    // Whether a person is opening a book right now: background conversion waits for them.
    internal sealed class ReadingActivity
    {
        private int _active;

        public bool IsBusy => Volatile.Read(ref _active) > 0;

        public IDisposable Begin()
        {
            Interlocked.Increment(ref _active);
            return new Scope(this);
        }

        private void End() => Interlocked.Decrement(ref _active);

        private sealed class Scope(ReadingActivity activity) : IDisposable
        {
            private int _done;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0)
                {
                    activity.End();
                }
            }
        }
    }

    // Turns one PDF of the library into an EPUB reading copy beside it, or says why it stays as it is. I/O stays outside transactions.
    internal sealed class ReadingDerivation
    {
        // The copy's name: the PDF's, with ".smart.epub"; it never takes the name of a book file somebody put there.
        private const string Suffix = ".smart.epub";
        private readonly StorageAccess _storage;

        public ReadingDerivation(StorageAccess storage)
        {
            _storage = storage;
        }

        public static string CopyPath(string pdfPath) => pdfPath[..^Path.GetExtension(pdfPath).Length] + Suffix;

        public async Task<ReadingDerivationOutcome> RunAsync(LogicContext context, ReadingDerivationSourceData source, CancellationToken ct)
        {
            StorageFiles files;
            Stream pdf;
            try
            {
                var mount = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadStorageMountAsync(source.StorageMountId, token), ct);
                files = _storage.Open(mount);
                pdf = await files.OpenReadAsync(StoragePath.Combine(source.RootRelativePath, source.RelativePath), ct);
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                return new ReadingDerivationOutcome(ReadingDerivationStatus.Failed, "offline");
            }

            // The PDF as the reader would convert it; the stricter judgement for a file that stays.
            byte[] hash;
            PdfBookContent book;
            await using (pdf)
            {
                try
                {
                    hash = await SHA256.HashDataAsync(pdf, ct);
                    pdf.Position = 0;
                    book = await Task.Run(() => PdfBook.Read(pdf), ct);
                }
                catch (PdfWithoutTextException)
                {
                    // A scan waits for the recognizer, where the Ocr module is on, without anybody opening it first.
                    var recognition = new ReadingRecognitionData(source.StoredFileId, source.SizeBytes, source.ModifiedAt);
                    await context.ExecuteBatchAsync((ctx, token) => ctx.RequestReadingRecognitionAsync(recognition, token), ct);
                    return new ReadingDerivationOutcome(ReadingDerivationStatus.Declined, "no_text");
                }
                catch (InvalidDataException)
                {
                    return new ReadingDerivationOutcome(ReadingDerivationStatus.Failed, "unreadable");
                }
                catch (Exception exception) when (exception is NasMountException or IOException)
                {
                    return new ReadingDerivationOutcome(ReadingDerivationStatus.Failed, "offline");
                }
            }

            var pages = book.Pages.Count;
            var figures = book.Chapters.SelectMany(chapter => chapter.Blocks).Count(block => block.Figure is not null);
            var counts = new ReadingDerivationOutcome(book.Ebook.IsReadable ? ReadingDerivationStatus.Ready : ReadingDerivationStatus.Declined, book.Ebook.Reason, hash, pages, book.Chapters.Count, figures);
            if (!book.Ebook.IsReadable)
            {
                return counts;
            }

            // A package a reader can open, with every chapter in it.
            var prepared = PdfBookHtml.Prepare(book, 0);
            var package = ReadingEbook.Build(new ReadingEbookContent(0, "urn:jularr:pdf:" + Convert.ToHexString(hash).ToLowerInvariant(), source.Title, source.Language ?? "und", null, ModifiedAt(source), false,
                [.. prepared.Sections.Select(section => section.Html)], [.. prepared.Sections.Select(section => section.Structure.Title)], null, [.. prepared.Toc],
                [.. book.Images.Select(image => new ReadingEbookResourceData(image.Path, image.MediaType, image.Bytes))]));
            if (!IsComplete(package, prepared.Sections.Count))
            {
                return counts with { Status = ReadingDerivationStatus.Failed, Reason = "invalid_package" };
            }

            // The new copy replaces the old one of this PDF; a file of that name that is not ours stays.
            var path = CopyPath(source.RelativePath);
            var target = StoragePath.Combine(source.RootRelativePath, path);
            try
            {
                if (source.EbookStoredFileId is { } old && source.EbookRelativePath is { } oldPath && oldPath.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
                {
                    if (await files.ExistsAsync(StoragePath.Combine(source.RootRelativePath, oldPath), ct))
                    {
                        await files.DeleteFileAsync(StoragePath.Combine(source.RootRelativePath, oldPath), ct);
                    }

                    await context.ExecuteBatchAsync(async (ctx, token) =>
                    {
                        await ctx.RemoveReadingDerivedFileAsync(new Jularr.Backend.Data.Logic.Library.StoredFileIdData(old), token);
                        return true;
                    }, ct);
                }

                using var content = new MemoryStream(package);
                if (await LibraryFileWriter.WriteAsync(files, source.RootRelativePath, path, content, ct) is not var (length, modifiedAt))
                {
                    return counts with { Status = ReadingDerivationStatus.Declined, Reason = "name_taken" };
                }

                var stored = await context.ExecuteBatchAsync((ctx, token) => ctx.SaveReadingDerivedFileAsync(new ReadingDerivedFileData(source.LibraryRootId, path, length, modifiedAt, source.MediaAssetId), token), ct);
                return counts with { EbookStoredFileId = stored.Id };
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                return counts with { Status = ReadingDerivationStatus.Failed, Reason = "write_failed", EbookStoredFileId = null };
            }
        }

        // A valid EPUB: it opens, its spine has every chapter and its text is there.
        private static bool IsComplete(byte[] package, int sections)
        {
            try
            {
                using var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
                var read = EpubReader.Read(archive);
                return read.Spine.Count == sections && read.Spine.All(item => archive.GetEntry(item.Path) is not null);
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        private static DateTime ModifiedAt(ReadingDerivationSourceData source) => DateTime.SpecifyKind(source.ModifiedAt, DateTimeKind.Utc);
    }

    // How an attempt ended: its status and reason, what the PDF held and the library file of the copy.
    internal sealed record ReadingDerivationOutcome(ReadingDerivationStatus Status, string? Reason, byte[]? Sha256 = null, int? Pages = null, int? Chapters = null, int? Figures = null,
        long? EbookStoredFileId = null);
}
