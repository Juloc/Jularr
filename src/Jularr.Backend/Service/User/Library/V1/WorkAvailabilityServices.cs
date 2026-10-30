using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Library.V1
{
    [ServiceEndpoint("GET", "/api/v1/library/availability")]
    public sealed class ReadWorkAvailabilityStatesV1 : UserService<ReadWorkAvailabilityStatesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WorkAvailabilityStateV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadWorkAvailabilityStatesParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadWorkAvailabilityStatesParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The same live facts as Library, bounded to the catalog page's known Works.
            var sql = new SqlStatement { DisableJit = true };
            sql.Statement = $$"""
                SELECT
                    work."Id",
                    acquisition."AvailabilityStateId",
                    availability."AvailableUnits",
                    availability."MonitoredUnits"
                FROM
                    unnest(@WorkIds::bigint[]) AS picked("Id")
                    JOIN "Works" AS work ON work."Id" = picked."Id"
                    JOIN LATERAL (
                        SELECT
                            profileWork."WorkId"
                        FROM
                            "ProfileWorks" AS profileWork
                        WHERE
                            profileWork."ProfileId" = @ProfileId
                            AND profileWork."WorkId" = work."Id"
                            AND NOT profileWork."IsHidden"
                        -- Keep the profile view parameterized by the selected Work.
                        OFFSET 0
                    ) AS visible ON TRUE
                    {{WorkCardProjection.CardJoins}}
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(LanguageCodes.Aliases);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<WorkAvailabilityStateV1>(await Context.ReadSql.ReadListAsync<WorkAvailabilityStateV1>(sql, CancellationToken));
        }
    }
}
