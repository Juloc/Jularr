using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Media.V1
{
    [ServiceEndpoint("GET", "/api/v1/works/{workId:long}")]
    public sealed class ReadWorkV1 : UserService<WorkParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Persisted metadata only.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    work."MediaTypeId",
                    work."CanonicalTitle",
                    (entry."WorkId" IS NOT NULL) AS "IsWatchlisted"
                FROM
                    "Works" AS work
                LEFT JOIN
                    "WatchlistEntries" AS entry ON entry."WorkId" = work."Id" AND entry."ProfileId" = @ProfileId
                WHERE
                    work."Id" = @WorkId;
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return await Context.ReadSql.ReadRequiredAsync<WorkDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/watchlist")]
    public sealed class ReadWatchlistV1 : UserService<ReadWatchlistParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<WorkDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadWatchlistParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadWatchlistParametersV1 parameters, Type resultType) => new("profile.media.read");
        protected override PageRequest GetPageRequest(ReadWatchlistParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // One profile-scoped page.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    work."MediaTypeId",
                    work."CanonicalTitle",
                    TRUE AS "IsWatchlisted"
                FROM
                    "WatchlistEntries" AS entry
                INNER JOIN
                    "Works" AS work ON work."Id" = entry."WorkId"
                WHERE
                    entry."ProfileId" = @ProfileId
                ORDER BY
                    work."CanonicalTitle",
                    work."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<WorkDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/watchlist/{workId:long}")]
    public sealed class UpdateWatchlistV1 : UserService<WorkParametersV1, UpdateWatchlistDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("profile.watchlist.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.UpdateWatchlistAsync(new WatchlistData(Auth.ProfileId!.Value, Parameters.WorkId), Data.IsWatchlisted, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
