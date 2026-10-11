using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Public.Localization.V1
{
    [ServiceEndpoint("GET", "/api/v1/localization/locales")]
    public sealed class ReadUiLocalesV1 : PublicService<ReadUiLocalesParametersV1>
    {
        private readonly UiLocalizationCache _cache;

        internal ReadUiLocalesV1(UiLocalizationCache cache)
        {
            _cache = cache;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() =>
            [ServiceResultType.Default<BaseDataList<UiLocaleDetailsV1>>()];

        protected override ModuleRequirement GetInstanceModules(ReadUiLocalesParametersV1 parameters) =>
            ModuleRequirement.None;

        protected override async Task<IServiceOutput> OnRead()
        {
            return await _cache.GetOrLoadAsync(0, async () =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        "Id",
                        "Locale",
                        "Name",
                        "IsRightToLeft"
                    FROM
                        "UiLocales"
                    WHERE
                        "IsEnabled" = TRUE
                        OR lower("Locale") = 'en'
                    ORDER BY
                        "Locale";
                    """;

                var locales = await Context.ReadSql.ReadListAsync<UiLocaleDetailsV1>(sql, CancellationToken);
                return new BaseDataList<UiLocaleDetailsV1>(locales);
            }, CancellationToken);
        }
    }
}
