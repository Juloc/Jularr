using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Reading.V1
{
    // The view of a book's PDF the profile chose for all its devices: the Smart Book, the original pages, or none for automatic again.
    [ServiceEndpoint("PUT", "/api/v1/reading/assets/{mediaAssetId:long}/view")]
    public sealed class SaveReadingViewV1 : UserService<ReadingAssetParametersV1, SaveReadingViewDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReadingAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingAssetParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveReadingViewAsync(new ReadingViewData(Auth.ProfileId!.Value, Parameters.MediaAssetId, Data.ReadingViewTypeId), CancellationToken);
            return new UpdateResult(true);
        }
    }

    // Where a book stands without opening it: the view its prepared text recommends, and the progress of a scan's recognition. The open reader asks now and then whether a better view came.
    [ServiceEndpoint("GET", "/api/v1/reading/assets/{mediaAssetId:long}/state")]
    public sealed class ReadReadingStateV1 : UserService<ReadingAssetParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReadingStateV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadingAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingAssetParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // A book's own EPUB or a ready reading copy is the Smart Book; a prepared reading text of the PDF says by its gate.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    pdf."Id" IS NOT NULL AS "HasPdf",
                    CASE
                        WHEN EXISTS (
                            SELECT
                                1
                            FROM
                                "ReadableStoredFiles" AS epub
                            WHERE
                                epub."MediaAssetId" = asset."Id"
                                AND epub."RelativePath" ~* '\.epub$'
                                AND epub."MissingSince" IS NULL
                                AND (NOT epub."IsDerived" OR epub."DerivedAnalyzerVersion" = @PdfAnalyzerVersion)
                        ) THEN 1
                        WHEN prepared."Id" IS NULL THEN NULL
                        WHEN coalesce((prepared."Structure" ->> 'Recommended')::boolean, TRUE) THEN 1
                        ELSE 2
                    END::smallint AS "RecommendedView",
                    recognition."Pages" AS "RecognitionPages",
                    coalesce((SELECT count(*) FROM "ReadingRecognizedPages" AS page WHERE page."StoredFileId" = recognition."StoredFileId"), 0)::integer AS "RecognizedPages",
                    coalesce(recognition."FailedAt" IS NULL AND recognition."FinishedAt" IS NULL, FALSE) AS "IsRecognizing"
                FROM
                    "MediaAssets" AS asset
                    JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                    LEFT JOIN LATERAL (
                        SELECT
                            file."Id",
                            file."SizeBytes",
                            file."ModifiedAt"
                        FROM
                            "StoredFiles" AS file
                        WHERE
                            file."MediaAssetId" = asset."Id"
                            AND file."RelativePath" ~* '\.pdf$'
                            AND file."MissingSince" IS NULL
                        ORDER BY
                            file."ModifiedAt" DESC,
                            file."Id"
                        LIMIT 1
                    ) AS pdf ON TRUE
                    LEFT JOIN "ReadingRecognitions" AS recognition ON recognition."StoredFileId" = pdf."Id"
                        AND recognition."SourceSizeBytes" = pdf."SizeBytes"
                        AND recognition."SourceModifiedAt" = pdf."ModifiedAt"
                    LEFT JOIN LATERAL (
                        SELECT
                            document."Id",
                            document."Structure"
                        FROM
                            "ReadingDocuments" AS document
                        WHERE
                            document."StoredFileId" = pdf."Id"
                            AND document."ReadingFormatTypeId" = 2
                            AND document."SourceSizeBytes" = pdf."SizeBytes"
                            AND document."SourceModifiedAt" = pdf."ModifiedAt"
                            AND document."AnalyzerVersion" = @PdfAnalyzerVersion
                            AND document."CacheCompletedAt" IS NOT NULL
                        ORDER BY
                            document."Id" DESC
                        LIMIT 1
                    ) AS prepared ON TRUE
                WHERE
                    asset."Id" = @MediaAssetId
                    AND asset."MediaAssetTypeId" IN (3, 4);
                """;
            sql.Parameters.Add(new ReadingSourceLookupData(Parameters.MediaAssetId, ReadingHtml.AnalyzerVersion, ComicPages.AnalyzerVersion, PdfBook.AnalyzerVersion));
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return await Context.ReadSql.ReadOptionalAsync<ReadingStateV1>(sql, CancellationToken) ?? throw new KeyNotFoundException("The book was not found.");
        }
    }

    // The anchor between the PDF's pages and the reading text: the section and block a page starts at, or the page a block stands on. A book whose text is not prepared has none.
    [ServiceEndpoint("GET", "/api/v1/reading/assets/{mediaAssetId:long}/position")]
    public sealed class ReadReadingPositionV1 : UserService<ReadingPositionParametersV1>
    {
        private const int MaximumScanned = 400;

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReadingPositionV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadingPositionParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingPositionParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Either a page, or a section and block.
            var byPage = Parameters.Page is not null;
            if (byPage == (Parameters.SectionIndex is not null && Parameters.Block is not null))
            {
                throw new DataValidationException(nameof(Parameters.Page), "invalid_value");
            }

            // The prepared text made from this book's PDF: the analysis of the PDF itself, or its reading copy.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    document."Id" AS "DocumentId",
                    document."Structure"::text AS "Structure"
                FROM
                    "ReadingDocuments" AS document
                    JOIN "StoredFiles" AS file ON file."Id" = document."StoredFileId"
                    JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                    JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                WHERE
                    asset."Id" = @MediaAssetId
                    AND document."CacheCompletedAt" IS NOT NULL
                    AND (
                        document."ReadingFormatTypeId" = 2 AND document."AnalyzerVersion" = @PdfAnalyzerVersion
                        OR EXISTS (SELECT 1 FROM "ReadingDerivations" AS derivation WHERE derivation."EbookStoredFileId" = file."Id" AND derivation."AnalyzerVersion" = @PdfAnalyzerVersion)
                    )
                ORDER BY
                    document."LastReadAt" DESC,
                    document."Id" DESC
                LIMIT 1;
                """;
            sql.Parameters.Add(new ReadingSourceLookupData(Parameters.MediaAssetId, ReadingHtml.AnalyzerVersion, ComicPages.AnalyzerVersion, PdfBook.AnalyzerVersion));
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            var prepared = await Context.ReadSql.ReadOptionalAsync<ReadingStructureTextData>(sql, CancellationToken) ?? throw new KeyNotFoundException("The text of the book was not found.");
            var structure = JsonSerializer.Deserialize<ReadingStructure>(prepared.Structure)!;

            async Task<string?> HtmlAsync(int section)
            {
                var read = new SqlStatement();
                read.Statement =
                    """
                    SELECT
                        @SectionIndex AS "SectionIndex",
                        NULL::text AS "Title",
                        convert_from(resource."Bytes", 'UTF8') AS "Html"
                    FROM
                        "ReadingResources" AS resource
                    WHERE
                        resource."ReadingDocumentId" = @DocumentId
                        AND resource."Path" = ':section/' || lpad(@SectionIndex::text, 5, '0');
                    """;
                read.Parameters.Add(new ReadingSectionParametersV1(prepared.DocumentId, section));
                return (await Context.ReadSql.ReadOptionalAsync<ReadingSectionTextData>(read, CancellationToken))?.Html;
            }

            if (Parameters.Page is { } page)
            {
                // The section that starts on or before the page, and in it the first block from that page on; a copy without page ranges is looked through.
                var ranged = structure.Sections.Any(section => section.PageFrom > 0);
                var first = ranged ? Math.Max(0, Array.FindLastIndex(structure.Sections, section => section.PageFrom is > 0 && section.PageFrom <= page)) : 0;
                for (var index = first; index < Math.Min(structure.Sections.Length, ranged ? first + 1 : MaximumScanned); index++)
                {
                    if (await HtmlAsync(index) is { } html && ReadingHtml.BlockOfPage(html, page) is { } block)
                    {
                        return new ReadingPositionV1(index, block, page);
                    }
                }

                return new ReadingPositionV1(first, 0, page);
            }

            var sectionIndex = Parameters.SectionIndex!.Value;
            if (sectionIndex >= structure.Sections.Length || await HtmlAsync(sectionIndex) is not { } sectionHtml)
            {
                throw new KeyNotFoundException("The section was not found.");
            }

            return new ReadingPositionV1(sectionIndex, Parameters.Block!.Value, ReadingHtml.PageOfBlock(sectionHtml, Parameters.Block!.Value) ?? Math.Max(structure.Sections[sectionIndex].PageFrom, 1));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/reading/assets/{mediaAssetId:long}/page-notes")]
    public sealed class ReadReadingPageNotesV1 : UserService<ReadingAssetParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<ReadingPageNoteV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadingAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingAssetParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The profile's own page notes in page order; the limit per book bounds them.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    note."Id",
                    note."PageNumber",
                    note."Anchor",
                    note."Note"
                FROM
                    "ReadingPageNotes" AS note
                WHERE
                    note."ProfileId" = @ProfileId
                    AND note."MediaAssetId" = @MediaAssetId
                ORDER BY
                    note."PageNumber",
                    note."Id"
                LIMIT 1000;
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<ReadingPageNoteV1>(await Context.ReadSql.ReadListAsync<ReadingPageNoteV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/reading/assets/{mediaAssetId:long}/page-notes")]
    public sealed class CreateReadingPageNoteV1 : UserService<ReadingAssetParametersV1, CreateReadingPageNoteDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateReadingPageNoteResultV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadingAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingAssetParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnCreate() =>
            new CreateReadingPageNoteResultV1(await Context.CreateReadingPageNoteAsync(new ReadingPageNoteSaveData(Auth.ProfileId!.Value, Parameters.MediaAssetId, Data.PageNumber, Data.Anchor, Data.Note),
                CancellationToken));
    }

    [ServiceEndpoint("DELETE", "/api/v1/reading/assets/{mediaAssetId:long}/page-notes/{readingPageNoteId:long}")]
    public sealed class DeleteReadingPageNoteV1 : UserService<ReadingPageNoteParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReadingPageNoteParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingPageNoteParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.DeleteReadingPageNoteAsync(new ReadingPageNoteKeyData(Auth.ProfileId!.Value, Parameters.MediaAssetId, Parameters.ReadingPageNoteId), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
