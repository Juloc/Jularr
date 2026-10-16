using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Media.V1
{
    [ServiceEndpoint("GET", "/api/v1/franchises/{franchiseId:long}")]
    public sealed class ReadFranchiseV1 : UserService<FranchiseParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<FranchiseV1>()];
        protected override ModuleRequirement GetInstanceModules(FranchiseParametersV1 parameters) => ModuleRequirement.All("Library", "Franchises");
        protected override ServicePermission GetPermission(FranchiseParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Only a series of which the profile may see a title.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    franchise."Id",
                    franchise."Name"
                FROM
                    "Franchises" AS franchise
                WHERE
                    franchise."Id" = @FranchiseId
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "ProfileWorks" AS visible
                            JOIN "Works" AS work ON work."Id" = visible."WorkId"
                        WHERE
                            visible."ProfileId" = @ProfileId
                            AND NOT visible."IsHidden"
                            AND work."FranchiseId" = franchise."Id"
                    );
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return await Context.ReadSql.ReadRequiredAsync<FranchiseV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/franchises/{franchiseId:long}/works")]
    public sealed class ReadFranchiseWorksV1 : UserService<FranchiseWorksParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<WorkCardV1>>()];
        protected override ModuleRequirement GetInstanceModules(FranchiseWorksParametersV1 parameters) => ModuleRequirement.All("Library", "Franchises");
        protected override ServicePermission GetPermission(FranchiseWorksParametersV1 parameters, Type resultType) => new("profile.media.read");
        protected override PageRequest GetPageRequest(FranchiseWorksParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // The series' titles the profile may see, in the series' order, then in release order.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    work."MediaTypeId",
                    visible."MediaSubtypeId",
                    visible."Title" AS "CanonicalTitle",
                    entry."WorkId" IS NOT NULL AS "IsWatchlisted",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    (
                        SELECT
                            fact."Value"
                        FROM
                            "WorkMetadataFacts" AS fact
                        WHERE
                            fact."WorkId" = work."Id"
                            AND fact."FactKey" = 'Poster'
                        ORDER BY
                            fact."IsManual" DESC
                        LIMIT 1
                    ) AS "PosterUrl"
                FROM
                    "ProfileWorks" AS visible
                    JOIN "Works" AS work ON work."Id" = visible."WorkId"
                    LEFT JOIN "WatchlistEntries" AS entry ON entry."WorkId" = work."Id" AND entry."ProfileId" = visible."ProfileId"
                WHERE
                    visible."ProfileId" = @ProfileId
                    AND NOT visible."IsHidden"
                    AND work."FranchiseId" = @FranchiseId
                ORDER BY
                    work."FranchisePosition" NULLS LAST,
                    work."ReleaseYear" NULLS LAST,
                    visible."Title",
                    work."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new FranchiseParametersV1(Parameters.FranchiseId));
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<WorkCardV1>(sql, CancellationToken);
        }
    }
}
