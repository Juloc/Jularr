using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Wanted.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/wanted/{wantedItemId:long}/languages")]
    public sealed class ReadWantedLanguagesV1 : AdminService<WantedItemParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WantedLanguagesV1>()];
        protected override ModuleRequirement GetInstanceModules(WantedItemParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(WantedItemParametersV1 parameters, Type resultType) => new("wanted.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    own."WantedItemId" IS NOT NULL AS "HasOwnChain",
                    own."FirstLanguage",
                    own."SecondLanguage",
                    coalesce(own."UsesOriginal", TRUE) AS "UsesOriginal",
                    ARRAY(SELECT chain."FirstLanguage"::text FROM "WantedItemChains" AS chain WHERE chain."WantedItemId" = wanted."Id" ORDER BY chain."Position") AS "Languages",
                    ARRAY(SELECT language."Language" FROM "WantedBookLanguages" AS language WHERE language."WantedItemId" = wanted."Id" AND NOT language."IsCovered" ORDER BY language."Position") AS "MissingLanguages"
                FROM
                    "WantedItems" AS wanted
                    LEFT JOIN "WantedItemLanguageChains" AS own ON own."WantedItemId" = wanted."Id"
                WHERE
                    wanted."Id" = @WantedItemId;
                """;
            sql.Parameters.Add(Parameters);
            return await Context.ReadSql.ReadRequiredAsync<WantedLanguagesV1>(sql, CancellationToken);
        }
    }

    // An administrator's own chain for one title, or none (the title follows its requests or the setting for monitored titles again).
    [ServiceEndpoint("PUT", "/api/v1/admin/wanted/{wantedItemId:long}/languages")]
    public sealed class SaveWantedLanguagesV1 : AdminService<WantedItemParametersV1, SaveWantedLanguagesDataV1>
    {
        private readonly AcquisitionSearchSignal _searchSignal;

        internal SaveWantedLanguagesV1(AcquisitionSearchSignal searchSignal)
        {
            _searchSignal = searchSignal;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WantedItemParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(WantedItemParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            if (Data.SecondLanguage is not null && (Data.FirstLanguage is null || Data.SecondLanguage == Data.FirstLanguage))
            {
                throw new DataValidationException(nameof(Data.SecondLanguage), "invalid_value");
            }

            var sql = new SqlStatement();
            sql.Statement = Data.FirstLanguage is null
                ? """DELETE FROM "WantedItemLanguageChains" AS own WHERE own."WantedItemId" = @WantedItemId;"""
                : """
                  INSERT INTO "WantedItemLanguageChains" ("WantedItemId", "FirstLanguage", "SecondLanguage", "UsesOriginal")
                  SELECT wanted."Id", @FirstLanguage, @SecondLanguage, @UsesOriginal FROM "WantedItems" AS wanted WHERE wanted."Id" = @WantedItemId
                  ON CONFLICT ("WantedItemId") DO UPDATE
                  SET "FirstLanguage" = EXCLUDED."FirstLanguage", "SecondLanguage" = EXCLUDED."SecondLanguage", "UsesOriginal" = EXCLUDED."UsesOriginal";
                  """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(Data);
            await Context.ExecuteAsyncForAffectedRows(sql, CancellationToken);
            return new UpdateResult(true);
        }

        protected override void OnCommitted() => _searchSignal.RequestSearch();
    }
}
