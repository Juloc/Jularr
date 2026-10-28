using System.Collections.Concurrent;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Reading.V1;

namespace Jularr.Backend.Service.System.Reading.V1
{
    // Prepares the library's books before anyone opens them, newest first: the same preparation as opening a book (text, pictures,
    // chapters, the book's language), a scan queued for recognition, so a book is ready to read and the translation job can take it
    // at once. One book per call; a person opening a book comes first. Answers whether it prepared one.
    public sealed class PrepareReadingBooksV1 : SystemService<ReadingPreparationParametersV1, NoData>
    {
        // Files that cannot be prepared (no text without the Ocr module, not a readable book) are not tried again until Jularr starts again or they change.
        private static readonly ConcurrentDictionary<(long StoredFileId, long SizeBytes, DateTime ModifiedAt), bool> Declined = new();

        private readonly ReadingContent _content;
        private readonly ReadingActivity _activity;

        internal PrepareReadingBooksV1(ReadingContent content, ReadingActivity activity)
        {
            _content = content;
            _activity = activity;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReadingPreparationParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingPreparationParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (_activity.IsBusy)
            {
                return new UpdateResult(false);
            }

            // Each book's file as a reader would open it (its own EPUB, a current reading copy, else the PDF) without a complete prepared
            // document of this version, on storage that is not missing and not waiting for recognition; newest books first.
            var books = await Context.ExecuteBatchAsync((ctx, ct) =>
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
                        FALSE AS "IsMissing",
                        root."RelativePath" AS "RootRelativePath",
                        mount."Id" AS "StorageMountId",
                        NULL::bigint AS "DocumentId",
                        FALSE AS "IsCached"
                    FROM
                        "MediaAssets" AS asset
                        JOIN "Works" AS work ON work."Id" = asset."WorkId"
                        JOIN LATERAL (
                            SELECT
                                candidate.*
                            FROM
                                "ReadableStoredFiles" AS candidate
                            WHERE
                                candidate."MediaAssetId" = asset."Id"
                                AND candidate."RelativePath" ~* '\.(epub|pdf)$'
                                AND (NOT candidate."IsDerived" OR candidate."DerivedAnalyzerVersion" = @PdfAnalyzerVersion)
                            ORDER BY
                                candidate."MissingSince" IS NULL DESC,
                                CASE WHEN candidate."RelativePath" ~* '\.pdf$' THEN 2 WHEN candidate."IsDerived" THEN 1 ELSE 0 END,
                                candidate."ModifiedAt" DESC,
                                candidate."Id"
                            LIMIT 1
                        ) AS file ON TRUE
                        JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                        JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                    WHERE
                        asset."MediaAssetTypeId" = 3
                        AND file."MissingSince" IS NULL
                        AND NOT EXISTS (
                            SELECT 1
                            FROM "ReadingDocuments" AS prepared
                            WHERE
                                prepared."StoredFileId" = file."Id"
                                AND prepared."SourceSizeBytes" = file."SizeBytes"
                                AND prepared."SourceModifiedAt" = file."ModifiedAt"
                                AND prepared."AnalyzerVersion" = CASE prepared."ReadingFormatTypeId" WHEN 2 THEN @PdfAnalyzerVersion ELSE @AnalyzerVersion END
                                AND prepared."CacheCompletedAt" IS NOT NULL)
                        AND NOT EXISTS (
                            SELECT 1
                            FROM "ReadingRecognitions" AS recognition
                            WHERE
                                recognition."StoredFileId" = file."Id"
                                AND recognition."SourceSizeBytes" = file."SizeBytes"
                                AND recognition."SourceModifiedAt" = file."ModifiedAt"
                                AND recognition."FinishedAt" IS NULL)
                    ORDER BY
                        asset."Id" DESC
                    LIMIT 20;
                    """;
                sql.Parameters.Add(new ReadingPreparationLookupData(ReadingHtml.AnalyzerVersion, PdfBook.AnalyzerVersion));
                return ctx.ReadSql.ReadListAsync<ReadingSourceData>(sql, ct);
            }, CancellationToken);

            var book = books.FirstOrDefault(candidate => !Declined.ContainsKey((candidate.StoredFileId, candidate.SizeBytes, candidate.ModifiedAt)));
            if (book is null)
            {
                return new UpdateResult(false);
            }

            try
            {
                await _content.OpenAsync(Context, book, CancellationToken);
            }
            catch (ServiceUnavailableException exception) when (exception.Code is "reading_pdf_recognizing")
            {
                // A scan now waits for the recognizer, which prepares it when it is done.
            }
            catch (ServiceUnavailableException exception) when (exception.Code is "reading_unavailable" or "storage_unavailable")
            {
                // Offline storage: the next round tries again.
                return new UpdateResult(false);
            }
            catch (ServiceUnavailableException)
            {
                Declined[(book.StoredFileId, book.SizeBytes, book.ModifiedAt)] = true;
            }

            return new UpdateResult(true);
        }
    }
}
