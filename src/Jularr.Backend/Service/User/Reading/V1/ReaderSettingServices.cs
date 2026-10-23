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
    [ServiceEndpoint("GET", "/api/v1/reading/settings")]
    public sealed class ReadReaderSettingsV1 : UserService<ReaderSettingsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReaderSettingsV1>()];
        protected override ModuleRequirement GetInstanceModules(ReaderSettingsParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReaderSettingsParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The profile's choices or the defaults of a profile without any.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    coalesce(setting."FontTypeId", 1)::smallint AS "FontTypeId",
                    coalesce(setting."Size", 20) AS "Size",
                    coalesce(setting."LineSpacing", 165) AS "LineSpacing",
                    coalesce(setting."ParagraphSpacing", 100) AS "ParagraphSpacing",
                    coalesce(setting."TextWidth", 68) AS "TextWidth",
                    coalesce(setting."Hyphenation", TRUE) AS "Hyphenation",
                    setting."ProfileId" IS NOT NULL AS "IsSaved"
                FROM
                    "Profiles" AS profile
                    LEFT JOIN "ProfileReaderSettings" AS setting ON setting."ProfileId" = profile."Id"
                WHERE
                    profile."Id" = @ProfileId;
                """;
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return await Context.ReadSql.ReadRequiredAsync<ReaderSettingsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/reading/settings")]
    public sealed class SaveReaderSettingsV1 : UserService<ReaderSettingsParametersV1, SaveReaderSettingsDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReaderSettingsParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReaderSettingsParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveReaderSettingsAsync(new ReaderSettingsSaveData(Auth.ProfileId!.Value, Data.FontTypeId, Data.Size, Data.LineSpacing, Data.ParagraphSpacing, Data.TextWidth, Data.Hyphenation),
                CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/reading/documents/{documentId:long}/offsets")]
    public sealed class ReadReadingSectionOffsetsV1 : UserService<ReadingDocumentParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<ReadingSectionOffsetV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadingDocumentParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingDocumentParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The structure stays even when the content left the cache; each section starts after the characters of the ones before it.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    (section.position - 1)::integer AS "SectionIndex",
                    coalesce((section.value ->> 'Characters')::bigint, 0) AS "Characters",
                    coalesce(sum((section.value ->> 'Characters')::bigint) OVER (ORDER BY section.position ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0)::bigint AS "CharactersBefore"
                FROM
                    "ReadingDocuments" AS document
                    CROSS JOIN LATERAL jsonb_array_elements(document."Structure" -> 'Sections') WITH ORDINALITY AS section (value, position)
                WHERE
                    document."Id" = @DocumentId
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "StoredFiles" AS file
                            JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                            JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                        WHERE
                            file."Id" = document."StoredFileId"
                    )
                ORDER BY
                    section.position
                LIMIT 5000;
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<ReadingSectionOffsetV1>(await Context.ReadSql.ReadListAsync<ReadingSectionOffsetV1>(sql, CancellationToken));
        }
    }
}
