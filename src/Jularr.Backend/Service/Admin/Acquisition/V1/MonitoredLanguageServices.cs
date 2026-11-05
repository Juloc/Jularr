using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    // The languages of the titles nobody requested: every profile's first language, or one fixed chain.
    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/monitored-languages")]
    public sealed class ReadMonitoredLanguagesV1 : AdminService<MonitoredLanguagesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<MonitoredLanguagesV1>()];
        protected override ModuleRequirement GetInstanceModules(MonitoredLanguagesParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(MonitoredLanguagesParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."MonitoredLanguageModeId" AS "ModeId",
                    settings."MonitoredFirstLanguage" AS "FirstLanguage",
                    settings."MonitoredSecondLanguage" AS "SecondLanguage",
                    settings."MonitoredUsesOriginal" AS "UsesOriginal",
                    ARRAY(SELECT DISTINCT chain."FirstLanguage"::text FROM "ProfileLanguages" AS chain ORDER BY 1) AS "Languages"
                FROM
                    "InstanceSettings" AS settings
                WHERE
                    settings."Id" = 1;
                """;
            return await Context.ReadSql.ReadRequiredAsync<MonitoredLanguagesV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/acquisition/monitored-languages")]
    public sealed class SaveMonitoredLanguagesV1 : AdminService<MonitoredLanguagesParametersV1, SaveMonitoredLanguagesDataV1>
    {
        private readonly AcquisitionSearchSignal _searchSignal;

        internal SaveMonitoredLanguagesV1(AcquisitionSearchSignal searchSignal)
        {
            _searchSignal = searchSignal;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(MonitoredLanguagesParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(MonitoredLanguagesParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // A fixed chain names its first language; a second needs a first and is another one.
            if (Data.ModeId == MonitoredLanguageMode.FixedChain && Data.FirstLanguage is null)
            {
                throw new DataValidationException(nameof(Data.FirstLanguage), "required");
            }

            if (Data.SecondLanguage is not null && (Data.FirstLanguage is null || Data.SecondLanguage == Data.FirstLanguage))
            {
                throw new DataValidationException(nameof(Data.SecondLanguage), "invalid_value");
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "InstanceSettings" AS settings
                SET
                    "MonitoredLanguageModeId" = @ModeId,
                    "MonitoredFirstLanguage" = @FirstLanguage,
                    "MonitoredSecondLanguage" = @SecondLanguage,
                    "MonitoredUsesOriginal" = @UsesOriginal
                WHERE
                    settings."Id" = 1;
                """;
            sql.Parameters.Add(Data);
            await Context.ExecuteAsyncForAffectedRows(sql, CancellationToken);
            return new UpdateResult(true);
        }

        // Another set of languages makes titles due that were complete.
        protected override void OnCommitted() => _searchSignal.RequestSearch();
    }
}
