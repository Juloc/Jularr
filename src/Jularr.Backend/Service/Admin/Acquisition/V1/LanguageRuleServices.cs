using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    // The add-ons of the languages: release groups and words preferred or blocked in releases of a language, for every media type.
    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/language-rules")]
    public sealed class ReadLanguageRulesV1 : AdminService<LanguageRulesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<LanguageRuleV1>>()];
        protected override ModuleRequirement GetInstanceModules(LanguageRulesParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(LanguageRulesParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    rule."Language",
                    rule."PreferredGroups"::text[] AS "PreferredGroups",
                    rule."BlockedGroups"::text[] AS "BlockedGroups",
                    rule."PreferredWords"::text[] AS "PreferredWords",
                    rule."BlockedWords"::text[] AS "BlockedWords"
                FROM
                    "LanguageReleaseRules" AS rule
                ORDER BY
                    rule."Language";
                """;
            return new BaseDataList<LanguageRuleV1>(await Context.ReadSql.ReadListAsync<LanguageRuleV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/acquisition/language-rules/{language}")]
    public sealed class SaveLanguageRuleV1 : AdminService<LanguageRuleParametersV1, SaveLanguageRuleDataV1>
    {
        private readonly AcquisitionSearchSignal _searchSignal;

        internal SaveLanguageRuleV1(AcquisitionSearchSignal searchSignal)
        {
            _searchSignal = searchSignal;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(LanguageRuleParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(LanguageRuleParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // An entry is trimmed and kept once in any case; every list empty takes the rule away.
            static string[] Clean(string[] entries) => [.. entries.Select(entry => entry.Trim()).Where(entry => entry.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
            var data = new SaveLanguageRuleDataV1(Clean(Data.PreferredGroups), Clean(Data.BlockedGroups), Clean(Data.PreferredWords), Clean(Data.BlockedWords));
            var sql = new SqlStatement();
            sql.Statement = data.PreferredGroups.Length + data.BlockedGroups.Length + data.PreferredWords.Length + data.BlockedWords.Length == 0
                ? """DELETE FROM "LanguageReleaseRules" AS rule WHERE rule."Language" = @Language;"""
                : """
                  INSERT INTO "LanguageReleaseRules" ("Language", "PreferredGroups", "BlockedGroups", "PreferredWords", "BlockedWords")
                  VALUES (@Language, @PreferredGroups, @BlockedGroups, @PreferredWords, @BlockedWords)
                  ON CONFLICT ("Language") DO UPDATE
                  SET "PreferredGroups" = EXCLUDED."PreferredGroups", "BlockedGroups" = EXCLUDED."BlockedGroups", "PreferredWords" = EXCLUDED."PreferredWords",
                      "BlockedWords" = EXCLUDED."BlockedWords", "Version" = "LanguageReleaseRules"."Version" + 1;
                  """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(data);
            await Context.ExecuteAsyncForAffectedRows(sql, CancellationToken);
            return new UpdateResult(true);
        }

        protected override void OnCommitted() => _searchSignal.RequestSearch();
    }
}
