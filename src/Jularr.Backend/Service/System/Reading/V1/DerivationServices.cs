using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Reading.V1;

namespace Jularr.Backend.Service.System.Reading.V1
{
    // Converts one digital PDF of the library per call into an EPUB reading copy beside it, or notes why it stays; returns whether there was one.
    // A person opening a book comes first: while one is opening, the call waits.
    public sealed class DeriveReadingEbooksV1 : SystemService<ReadingDerivationParametersV1, NoData>
    {
        private readonly ReadingDerivation _derivation;
        private readonly ReadingActivity _activity;

        internal DeriveReadingEbooksV1(ReadingDerivation derivation, ReadingActivity activity)
        {
            _derivation = derivation;
            _activity = activity;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReadingDerivationParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "SmartPdf");
        protected override ServicePermission GetPermission(ReadingDerivationParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (_activity.IsBusy)
            {
                return new UpdateResult(false);
            }

            // A PDF without a current copy, on storage that is not known to be offline, unless its book has an EPUB of its own.
            var source = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        file."Id" AS "StoredFileId",
                        file."MediaAssetId",
                        file."LibraryRootId",
                        file."RelativePath",
                        file."SizeBytes",
                        file."ModifiedAt",
                        root."RelativePath" AS "RootRelativePath",
                        mount."Id" AS "StorageMountId",
                        work."CanonicalTitle" AS "Title",
                        work."OriginalLanguage" AS "Language",
                        ebook."Id" AS "EbookStoredFileId",
                        ebook."RelativePath" AS "EbookRelativePath"
                    FROM
                        "StoredFiles" AS file
                        JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId" AND asset."MediaAssetTypeId" IN (3, 4)
                        JOIN "Works" AS work ON work."Id" = asset."WorkId"
                        JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                        JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                        LEFT JOIN "StorageMountStates" AS state ON state."StorageMountId" = mount."Id"
                        LEFT JOIN "ReadingDerivations" AS derivation ON derivation."StoredFileId" = file."Id"
                        LEFT JOIN "StoredFiles" AS ebook ON ebook."Id" = derivation."EbookStoredFileId"
                    WHERE
                        file."RelativePath" ~* '\.pdf$'
                        AND file."MissingSince" IS NULL
                        AND file."SizeBytes" <= 1073741824
                        AND coalesce(state."StorageAvailabilityTypeId", 1) = 1
                        AND NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "StoredFiles" AS other
                            WHERE
                                other."MediaAssetId" = file."MediaAssetId"
                                AND other."RelativePath" ~* '\.epub$'
                                AND other."MissingSince" IS NULL
                                AND NOT EXISTS (SELECT 1 FROM "ReadingDerivations" AS derived WHERE derived."EbookStoredFileId" = other."Id")
                        )
                        AND (
                            derivation."StoredFileId" IS NULL
                            OR derivation."SourceSizeBytes" <> file."SizeBytes"
                            OR derivation."SourceModifiedAt" <> file."ModifiedAt"
                            OR derivation."AnalyzerVersion" <> @AnalyzerVersion
                            OR derivation."ReadingDerivationStatusTypeId" = 2 AND (derivation."EbookStoredFileId" IS NULL OR ebook."MissingSince" IS NOT NULL)
                            OR derivation."ReadingDerivationStatusTypeId" = 4 AND derivation."Reason" = 'offline' AND derivation."AttemptedAt" < now() - interval '15 minutes'
                            OR derivation."ReadingDerivationStatusTypeId" = 4 AND derivation."Attempts" < 5 AND derivation."AttemptedAt" < now() - interval '6 hours'
                            OR derivation."ReadingDerivationStatusTypeId" = 1 AND derivation."AttemptedAt" < now() - interval '1 hour'
                        )
                    ORDER BY
                        derivation."StoredFileId" IS NULL DESC,
                        file."Id"
                    LIMIT 1;
                    """;
                sql.Parameters.Add(new ReadingAnalyzerData(PdfBook.AnalyzerVersion));
                return ctx.ReadSql.ReadOptionalAsync<ReadingDerivationSourceData>(sql, ct);
            }, CancellationToken);
            if (source is null)
            {
                return new UpdateResult(false);
            }

            var start = new ReadingDerivationStartData(source.StoredFileId, source.SizeBytes, source.ModifiedAt, PdfBook.AnalyzerVersion);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.StartReadingDerivationAsync(start, ct);
                return true;
            }, CancellationToken);

            // A fault of this file is its own failure and does not stop the others.
            ReadingDerivationOutcome outcome;
            try
            {
                outcome = await _derivation.RunAsync(Context, source, CancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && !CancellationToken.IsCancellationRequested)
            {
                outcome = new ReadingDerivationOutcome(ReadingDerivationStatus.Failed, "error");
            }

            var result = new ReadingDerivationResultData(source.StoredFileId, source.SizeBytes, source.ModifiedAt, outcome.Sha256, PdfBook.AnalyzerVersion, outcome.Status, outcome.Reason, outcome.Pages,
                outcome.Chapters, outcome.Figures, outcome.Status == ReadingDerivationStatus.Ready ? outcome.EbookStoredFileId : null);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.FinishReadingDerivationAsync(result, ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
