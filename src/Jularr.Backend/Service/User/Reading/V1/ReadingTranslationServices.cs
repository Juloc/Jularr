using System.Text;
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
    [ServiceEndpoint("GET", "/api/v1/reading/documents/{documentId:long}/sections/{sectionIndex:int}/translations/{languageTypeId}")]
    public sealed class ReadReadingTranslationV1 : UserService<ReadingTranslationParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReadingTranslationV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadingTranslationParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(ReadingTranslationParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // A section another reader translated before.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    convert_from(resource."Bytes", 'UTF8') AS "Blocks"
                FROM
                    "ReadingResources" AS resource
                WHERE
                    resource."ReadingDocumentId" = @DocumentId
                    AND resource."Path" = @Path
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "ReadingDocuments" AS document
                            JOIN "StoredFiles" AS file ON file."Id" = document."StoredFileId"
                            JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                            JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                        WHERE
                            document."Id" = resource."ReadingDocumentId"
                    );
                """;
            sql.Parameters.Add(new ReadingTranslationPathData(Parameters.DocumentId, ReadingTranslations.Path(Parameters.SectionIndex, Parameters.LanguageTypeId)));
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return await Context.ReadSql.ReadOptionalAsync<ReadingTranslationV1>(sql, CancellationToken) ?? throw new KeyNotFoundException("The section is not translated yet.");
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/reading/documents/{documentId:long}/sections/{sectionIndex:int}/translations/{languageTypeId}")]
    public sealed class SaveReadingTranslationV1 : UserService<ReadingTranslationParametersV1, SaveReadingTranslationDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReadingTranslationParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(ReadingTranslationParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // One plain text per block of the section, kept beside it; the reader only ever sets them as text.
            var blocks = ReadingTranslations.Parse(Data.Blocks) ?? throw new DataValidationException(nameof(Data.Blocks), "translation_mismatch");
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    (document."Structure" -> 'Sections' -> @SectionIndex ->> 'Blocks')::integer AS "Blocks"
                FROM
                    "ReadingDocuments" AS document
                WHERE
                    document."Id" = @DocumentId
                    AND document."CacheCompletedAt" IS NOT NULL
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "StoredFiles" AS file
                            JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                            JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                        WHERE
                            file."Id" = document."StoredFileId"
                    );
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            var section = await Context.Sql.ReadOptionalAsync<ReadingSectionBlocksData>(sql, CancellationToken) ?? throw new KeyNotFoundException("The section was not found.");
            if (section.Blocks != blocks.Count)
            {
                throw new DataValidationException(nameof(Data.Blocks), "translation_mismatch");
            }

            await Context.SaveReadingResourceAsync(new ReadingResourceData(Parameters.DocumentId, ReadingTranslations.Path(Parameters.SectionIndex, Parameters.LanguageTypeId),
                "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(blocks))), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
