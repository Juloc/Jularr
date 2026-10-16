using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Reading.V1;

namespace Jularr.Backend.Service.System.Reading.V1
{
    // Recognizes the text of one queued scanned PDF per call and prepares it like any book; returns whether there was one.
    public sealed class RecognizeReadingV1 : SystemService<ReadingRecognitionParametersV1, NoData>
    {
        private readonly ReadingContent _content;
        private readonly IPageRecognizer _recognizer;

        internal RecognizeReadingV1(ReadingContent content, IPageRecognizer recognizer)
        {
            _content = content;
            _recognizer = recognizer;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReadingRecognitionParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Ocr");
        protected override ServicePermission GetPermission(ReadingRecognitionParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // The oldest waiting scan on storage that is not known to be missing.
            var queued = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        recognition."StoredFileId",
                        work."OriginalLanguage" AS "Language"
                    FROM
                        "ReadingRecognitions" AS recognition
                        JOIN "StoredFiles" AS file ON file."Id" = recognition."StoredFileId"
                        JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                        JOIN "Works" AS work ON work."Id" = asset."WorkId"
                    WHERE
                        recognition."FailedAt" IS NULL
                        AND file."MissingSince" IS NULL
                    ORDER BY
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

            // Offline storage or an unreachable model host waits for the next round; a file that holds no readable text even so stays a PDF.
            var failed = false;
            try
            {
                var recognize = await _recognizer.RecognizerAsync(queued.Language, CancellationToken);
                await _content.OpenAsync(Context, source, CancellationToken, recognize);
            }
            catch (ServiceUnavailableException exception) when (exception.Code is "reading_unavailable" or "ocr_unavailable")
            {
                return new UpdateResult(false);
            }
            catch (ServiceUnavailableException)
            {
                failed = true;
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.FinishReadingRecognitionAsync(new ReadingRecognitionResultData(queued.StoredFileId, failed), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
