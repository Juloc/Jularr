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
    [ServiceEndpoint("GET", "/api/v1/reading/assets/{mediaAssetId:long}/annotations")]
    public sealed class ReadReadingAnnotationsV1 : UserService<ReadingAnnotationsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<ReadingAnnotationV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadingAnnotationsParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingAnnotationsParametersV1 parameters, Type resultType) => new("profile.media.read");
        protected override PageRequest GetPageRequest(ReadingAnnotationsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // The profile's own notes in reading order.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    annotation."Id",
                    annotation."SectionIndex",
                    annotation."StartBlock",
                    annotation."StartOffset",
                    annotation."EndBlock",
                    annotation."EndOffset",
                    annotation."Quote",
                    annotation."Note",
                    annotation."HighlightColorTypeId"
                FROM
                    "ReadingAnnotations" AS annotation
                WHERE
                    annotation."ProfileId" = @ProfileId
                    AND annotation."MediaAssetId" = @MediaAssetId
                ORDER BY
                    annotation."SectionIndex",
                    annotation."StartBlock",
                    annotation."StartOffset",
                    annotation."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new ReadingAssetParametersV1(Parameters.MediaAssetId));
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<ReadingAnnotationV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/reading/assets/{mediaAssetId:long}/sections/{sectionIndex:int}/annotations")]
    public sealed class ReadReadingSectionAnnotationsV1 : UserService<ReadingSectionAnnotationsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<ReadingAnnotationV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadingSectionAnnotationsParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingSectionAnnotationsParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // What the open section marks; the limit per book bounds it.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    annotation."Id",
                    annotation."SectionIndex",
                    annotation."StartBlock",
                    annotation."StartOffset",
                    annotation."EndBlock",
                    annotation."EndOffset",
                    annotation."Quote",
                    annotation."Note",
                    annotation."HighlightColorTypeId"
                FROM
                    "ReadingAnnotations" AS annotation
                WHERE
                    annotation."ProfileId" = @ProfileId
                    AND annotation."MediaAssetId" = @MediaAssetId
                    AND annotation."SectionIndex" = @SectionIndex
                ORDER BY
                    annotation."StartBlock",
                    annotation."StartOffset",
                    annotation."Id"
                LIMIT 1000;
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<ReadingAnnotationV1>(await Context.ReadSql.ReadListAsync<ReadingAnnotationV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/reading/assets/{mediaAssetId:long}/annotations")]
    public sealed class CreateReadingAnnotationV1 : UserService<ReadingAssetParametersV1, CreateReadingAnnotationDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateReadingAnnotationResultV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadingAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingAssetParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnCreate() =>
            new CreateReadingAnnotationResultV1(await Context.CreateReadingAnnotationAsync(new ReadingAnnotationSaveData(Auth.ProfileId!.Value, Parameters.MediaAssetId, Data.SectionIndex,
                Data.StartBlock, Data.StartOffset, Data.EndBlock, Data.EndOffset, Data.Quote, Data.Note, Data.HighlightColorTypeId), CancellationToken));
    }

    [ServiceEndpoint("PUT", "/api/v1/reading/assets/{mediaAssetId:long}/annotations/{readingAnnotationId:long}")]
    public sealed class UpdateReadingAnnotationV1 : UserService<ReadingAnnotationParametersV1, UpdateReadingAnnotationDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReadingAnnotationParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingAnnotationParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.UpdateReadingAnnotationAsync(new ReadingAnnotationUpdateData(Auth.ProfileId!.Value, Parameters.MediaAssetId, Parameters.ReadingAnnotationId, Data.Note,
                Data.HighlightColorTypeId), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/reading/assets/{mediaAssetId:long}/annotations/{readingAnnotationId:long}")]
    public sealed class DeleteReadingAnnotationV1 : UserService<ReadingAnnotationParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReadingAnnotationParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingAnnotationParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.DeleteReadingAnnotationAsync(new ReadingAnnotationKeyData(Auth.ProfileId!.Value, Parameters.MediaAssetId, Parameters.ReadingAnnotationId), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
