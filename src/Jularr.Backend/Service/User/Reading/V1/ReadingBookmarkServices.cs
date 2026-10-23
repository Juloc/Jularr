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
    [ServiceEndpoint("GET", "/api/v1/reading/assets/{mediaAssetId:long}/bookmarks")]
    public sealed class ReadReadingBookmarksV1 : UserService<ReadingAssetParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<ReadingBookmarkV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadingAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingAssetParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The profile's own bookmarks in reading order; the limit per book bounds it.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    bookmark."Id",
                    bookmark."SectionIndex",
                    bookmark."Block",
                    bookmark."Progression",
                    bookmark."Title"
                FROM
                    "ReadingBookmarks" AS bookmark
                WHERE
                    bookmark."ProfileId" = @ProfileId
                    AND bookmark."MediaAssetId" = @MediaAssetId
                ORDER BY
                    bookmark."SectionIndex",
                    bookmark."Block"
                LIMIT 200;
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<ReadingBookmarkV1>(await Context.ReadSql.ReadListAsync<ReadingBookmarkV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/reading/assets/{mediaAssetId:long}/bookmarks")]
    public sealed class CreateReadingBookmarkV1 : UserService<ReadingAssetParametersV1, CreateReadingBookmarkDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateReadingBookmarkResultV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadingAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingAssetParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnCreate() =>
            new CreateReadingBookmarkResultV1(await Context.SaveReadingBookmarkAsync(new ReadingBookmarkSaveData(Auth.ProfileId!.Value, Parameters.MediaAssetId, Data.SectionIndex, Data.Block,
                Data.Progression, Data.Title), CancellationToken));
    }

    [ServiceEndpoint("DELETE", "/api/v1/reading/assets/{mediaAssetId:long}/bookmarks/{readingBookmarkId:long}")]
    public sealed class DeleteReadingBookmarkV1 : UserService<ReadingBookmarkParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReadingBookmarkParametersV1 parameters) => ModuleRequirement.All("Library", "Reading");
        protected override ServicePermission GetPermission(ReadingBookmarkParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.DeleteReadingBookmarkAsync(new ReadingBookmarkKeyData(Auth.ProfileId!.Value, Parameters.MediaAssetId, Parameters.ReadingBookmarkId), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
