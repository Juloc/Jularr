using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Reading.V1;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Pdf;
using Jularr.Integrations.Logic.Storage;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Jularr.Backend.Service.System.Reading.V1
{
    // Reads a few pages of one queued scanned PDF per call, the page a reader has open first, and rests as long as it worked (longer while Jularr is in use) so the server stays free.
    // Pages already read are kept, so an interrupted scan goes on where it stopped; the last slice prepares the reading text. Answers whether it worked.
    public sealed class RecognizeReadingV1 : SystemService<ReadingRecognitionParametersV1, NoData>
    {
        private readonly ReadingContent _content;
        private readonly ReadingActivity _activity;
        private readonly IPageRecognizer _recognizer;
        private readonly StorageAccess _storage;
        private readonly UserActivity _use;
        private readonly TimeProvider _time;

        internal RecognizeReadingV1(ReadingContent content, ReadingActivity activity, IPageRecognizer recognizer, StorageAccess storage, UserActivity use, TimeProvider time)
        {
            _content = content;
            _activity = activity;
            _recognizer = recognizer;
            _storage = storage;
            _use = use;
            _time = time;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReadingRecognitionParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Ocr");
        protected override ServicePermission GetPermission(ReadingRecognitionParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // A person opening a book comes first.
            if (_activity.IsBusy)
            {
                return new UpdateResult(false);
            }

            // The scan a reader has open, else the oldest waiting one, on storage that is not known to be missing.
            var queued = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        recognition."StoredFileId",
                        work."OriginalLanguage" AS "Language",
                        recognition."Pages",
                        recognition."WantedPage"
                    FROM
                        "ReadingRecognitions" AS recognition
                        JOIN "StoredFiles" AS file ON file."Id" = recognition."StoredFileId"
                        JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                        JOIN "Works" AS work ON work."Id" = asset."WorkId"
                    WHERE
                        recognition."FailedAt" IS NULL
                        AND recognition."FinishedAt" IS NULL
                        AND recognition."SourceSizeBytes" = file."SizeBytes"
                        AND recognition."SourceModifiedAt" = file."ModifiedAt"
                        AND file."MissingSince" IS NULL
                    ORDER BY
                        recognition."WantedAt" DESC NULLS LAST,
                        recognition."RequestedAt",
                        recognition."StoredFileId"
                    LIMIT 1;
                    """;
                return ctx.ReadSql.ReadOptionalAsync<ReadingRecognitionQueueData>(sql, ct);
            }, CancellationToken);
            if (queued is null)
            {
                return new UpdateResult(false);
            }

            var source = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        work."Id" AS "WorkId",
                        work."CanonicalTitle" AS "Title",
                        file."Id" AS "StoredFileId",
                        file."RelativePath",
                        file."SizeBytes",
                        file."ModifiedAt",
                        file."MissingSince" IS NOT NULL AS "IsMissing",
                        root."RelativePath" AS "RootRelativePath",
                        mount."Id" AS "StorageMountId",
                        NULL::bigint AS "DocumentId",
                        FALSE AS "IsCached"
                    FROM
                        "StoredFiles" AS file
                        JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                        JOIN "Works" AS work ON work."Id" = asset."WorkId"
                        JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                        JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                    WHERE
                        file."Id" = @StoredFileId;
                    """;
                sql.Parameters.Add(queued);
                return ctx.ReadSql.ReadRequiredAsync<ReadingSourceData>(sql, ct);
            }, CancellationToken);

            // Offline storage or an unreachable model host waits for the next round; a file that cannot be read at all fails until it changes.
            var started = _time.GetTimestamp();
            bool isFinished;
            try
            {
                var recognize = await _recognizer.RecognizerAsync(queued.Language, CancellationToken);
                isFinished = await ReadSliceAsync(source, queued, recognize);
            }
            catch (ServiceUnavailableException exception) when (exception.Code is "reading_unavailable" or "ocr_unavailable")
            {
                return new UpdateResult(false);
            }
            catch (InvalidDataException)
            {
                await FinishAsync(queued.StoredFileId, true);
                return new UpdateResult(true);
            }

            if (isFinished)
            {
                await FinishAsync(queued.StoredFileId, false);

                // The text of the PDF is built again with what was read; a scan that still holds nothing readable stays with the original pages.
                await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    await ctx.ResetReadingDocumentsAsync(new StoredFileIdData(queued.StoredFileId), ct);
                    return true;
                }, CancellationToken);
                try
                {
                    await _content.OpenAsync(Context, source, CancellationToken);
                }
                catch (ServiceUnavailableException)
                {
                    // Not fit to read as text: the original pages remain the way to read it.
                }

                return new UpdateResult(true);
            }

            // Rest before the next slice: as long as this one took, longer while somebody uses Jularr.
            await Task.Delay(ReadingRecognitionPlan.Rest(_time.GetElapsedTime(started), _use.IsUsedWithin(TimeSpan.FromSeconds(30))), _time, CancellationToken);
            return new UpdateResult(true);
        }

        // The next pages in order, until the slice is full or its time is up; answers whether every page of the PDF has been read.
        private async Task<bool> ReadSliceAsync(ReadingSourceData source, ReadingRecognitionQueueData queued, Func<SKBitmap, Jularr.Integrations.Logic.Ocr.OcrPage> recognize)
        {
            // PDFium draws the pages from the file's bytes, PdfPig tells which pages are scans.
            if (source.SizeBytes > PdfBook.MaximumRenderBytes)
            {
                throw new InvalidDataException("The PDF is too large to draw.");
            }

            byte[] bytes;
            try
            {
                var mount = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadStorageMountAsync(source.StorageMountId, ct), CancellationToken);
                await using var file = await _storage.Open(mount).OpenReadAsync(StoragePath.Combine(source.RootRelativePath, source.RelativePath), CancellationToken);
                using var copy = new MemoryStream();
                await file.CopyToAsync(copy, CancellationToken);
                bytes = copy.ToArray();
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                // Offline storage is not a failed scan.
                throw new ServiceUnavailableException("reading_unavailable");
            }

            using (var rendering = PdfRendering.Open(bytes))
            {
                PdfDocument pdf;
                try
                {
                    pdf = PdfDocument.Open(bytes);
                }
                catch (Exception exception) when (exception is UglyToad.PdfPig.Core.PdfDocumentFormatException or ArgumentException or InvalidOperationException or NotSupportedException or IndexOutOfRangeException)
                {
                    throw new InvalidDataException("The PDF cannot be read.", exception);
                }

                using (pdf)
                {
                    var pages = pdf.NumberOfPages;
                    if (pages > PdfBook.MaximumPages)
                    {
                        throw new InvalidDataException("The PDF has too many pages.");
                    }

                    if (queued.Pages != pages)
                    {
                        await Context.ExecuteBatchAsync(async (ctx, ct) =>
                        {
                            await ctx.SetReadingRecognitionPagesAsync(new ReadingRecognitionPagesData(queued.StoredFileId, pages), ct);
                            return true;
                        }, CancellationToken);
                    }

                    var done = (await Context.ExecuteBatchAsync((ctx, ct) =>
                    {
                        var sql = new SqlStatement();
                        sql.Statement =
                            """
                            SELECT
                                page."PageNumber"
                            FROM
                                "ReadingRecognizedPages" AS page
                            WHERE
                                page."StoredFileId" = @Id;
                            """;
                        sql.Parameters.Add(new StoredFileIdData(queued.StoredFileId));
                        return ctx.ReadSql.ReadListAsync<ReadingRecognizedPageNumberData>(sql, ct);
                    }, CancellationToken)).Select(page => page.PageNumber).ToHashSet();

                    // The slice: a bounded number of pages within a bounded time; the page work runs off the request's thread.
                    var started = _time.GetTimestamp();
                    var read = 0;
                    foreach (var number in ReadingRecognitionPlan.Remaining(pages, queued.WantedPage, done))
                    {
                        if (read == ReadingRecognitionPlan.SlicePages || _time.GetElapsedTime(started) >= ReadingRecognitionPlan.SliceTime)
                        {
                            return false;
                        }

                        RecognizedPage found;
                        try
                        {
                            found = await Task.Run(() => PdfScanPages.Read(pdf.GetPage(number), rendering, recognize), CancellationToken);
                        }
                        catch (Exception exception) when (exception is UglyToad.PdfPig.Core.PdfDocumentFormatException or ArgumentException or InvalidOperationException or NotSupportedException
                            or IndexOutOfRangeException or KeyNotFoundException or NullReferenceException or InvalidCastException)
                        {
                            // A page that cannot be read is left to the original.
                            found = new RecognizedPage(RecognizedPage.Undecodable, 1, []);
                        }

                        var save = new ReadingRecognizedPageData(queued.StoredFileId, number, found.Reason, (decimal)found.Quality, RecognizedPage.Serialize(found.Lines));
                        await Context.ExecuteBatchAsync(async (ctx, ct) =>
                        {
                            await ctx.SaveReadingRecognizedPageAsync(save, ct);
                            return true;
                        }, CancellationToken);
                        read++;
                    }

                    return true;
                }
            }
        }

        private async Task FinishAsync(long storedFileId, bool isFailed)
        {
            var result = new ReadingRecognitionResultData(storedFileId, isFailed);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.FinishReadingRecognitionAsync(result, ct);
                return true;
            }, CancellationToken);
        }
    }
}
