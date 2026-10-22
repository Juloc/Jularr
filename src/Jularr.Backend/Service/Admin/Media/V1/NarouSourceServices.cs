using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Narou;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // Explicitly associate a public Narō web novel with a LightNovel Work.
    // This is deliberately NOT an implicit title-based link to a published LN.
    public sealed class SetNarouSourceDataV1 : BaseData
    {
        public SetNarouSourceDataV1(string ncode) => Ncode = ncode;

        [DataField(typeof(WorkFields), nameof(WorkFields.MatchKey))]
        public string Ncode { get; }
    }
    internal sealed record NarouSourceIdentityData(long WorkId, string Ncode, MediaType MediaTypeId, MediaSubtype MediaSubtypeId);

    [ServiceEndpoint("PUT", "/api/v1/admin/works/{workId:long}/narou")]
    public sealed class SetNarouSourceV1 : AdminService<WorkParametersV1, SetNarouSourceDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) =>
            ModuleRequirement.All("Library", "Acquisition");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) =>
            new("media.manual.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            if (!NarouClient.IsNcode(Data.Ncode))
                throw new DataValidationException(nameof(Data.Ncode), "invalid_transition");

            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value", "IsManual")
                SELECT work."Id", 'syosetu', @Ncode, TRUE
                FROM "Works" AS work
                WHERE work."Id" = @WorkId
                  AND work."MediaTypeId" = @MediaTypeId
                  AND work."MediaSubtypeId" = @MediaSubtypeId
                ON CONFLICT ("WorkId", "Namespace")
                DO UPDATE SET "Value" = EXCLUDED."Value", "IsManual" = TRUE, "UpdatedAt" = now();
                """;
            sql.Parameters.Add(new NarouSourceIdentityData(
                Parameters.WorkId, Data.Ncode.ToLowerInvariant(), MediaType.Book, MediaSubtype.LightNovel));
            if (await Context.ExecuteAsyncForAffectedRows(sql, CancellationToken) == 0)
                throw new DataValidationException(nameof(Parameters.WorkId), "invalid_transition");
            return new UpdateResult(true);
        }
    }
}
