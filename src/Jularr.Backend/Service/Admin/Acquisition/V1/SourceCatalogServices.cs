using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/sources")]
    public sealed class ReadSourceCatalogV1 : AdminService<SourceCatalogParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<SourceCatalogItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(SourceCatalogParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(SourceCatalogParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnRead()
        {
            var state = await SourceCatalogState.ReadAsync(Context.ReadSql, CancellationToken);
            return new BaseDataList<SourceCatalogItemV1>([.. SourceCatalog.Entries.Select(entry => new SourceCatalogItemV1(entry.Key, entry.Name, entry.Group,
                Enum.GetValues<SourceCapability>().Where(capability => capability != SourceCapability.None && entry.Capabilities.HasFlag(capability)).Select(capability => capability.ToString()).ToArray(),
                entry.Availability, entry.ConnectionType, entry.DefaultBaseUrl, state.IsConfigured(entry)))]);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/acquisition/sources/setup")]
    public sealed class SetUpSourcesV1 : AdminService<SourceCatalogParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SourceSetupResultV1>()];
        protected override ModuleRequirement GetInstanceModules(SourceCatalogParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(SourceCatalogParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Keyless ready sources are added; accounts and keys are asked for, never invented.
            return await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var state = await SourceCatalogState.ReadAsync(ctx.ReadSql, ct);
                var added = new List<string>();
                var needsAccount = new List<string>();
                foreach (var entry in SourceCatalog.Entries.Where(entry => !state.IsConfigured(entry)))
                {
                    if (entry.Availability == SourceAvailability.Account)
                    {
                        needsAccount.Add(entry.Key);
                    }
                    else if (entry is { Availability: SourceAvailability.Ready, ConnectionType: { } type, DefaultBaseUrl: { } baseUrl })
                    {
                        await ctx.CreateAcquisitionConnectionAsync(new AcquisitionConnectionSaveData(null, entry.Name, type, baseUrl, null, true, 50, [], null, null, null, null, null, null), ct);
                        added.Add(entry.Key);
                    }
                    else if (entry is { Availability: SourceAvailability.Ready, ProviderKey: { } provider })
                    {
                        await ctx.EnableProviderAsync(provider, ct);
                        added.Add(entry.Key);
                    }
                }

                return new SourceSetupResultV1([.. added], [.. needsAccount]);
            }, CancellationToken);
        }
    }

    internal sealed class SourceCatalogState(IReadOnlyList<SourceConnectionData> connections, IReadOnlyList<SourceProviderData> providers)
    {
        // A source counts as set up when a connection points at its default address, any other connection of its kind counts for the generic entries.
        public bool IsConfigured(SourceCatalogEntry entry) => entry switch
        {
            { ProviderKey: { } key } => providers.Any(provider => provider.Key == key && provider.IsEnabled),
            { ConnectionType: { } type, DefaultBaseUrl: { } baseUrl } => connections.Any(connection => connection.AcquisitionConnectionTypeId == type && Points(connection, baseUrl)),
            { ConnectionType: { } type } => connections.Any(connection => connection.AcquisitionConnectionTypeId == type
                && !SourceCatalog.Entries.Any(known => known.DefaultBaseUrl is { } baseUrl && Points(connection, baseUrl))),
            _ => false
        };

        private static bool Points(SourceConnectionData connection, string baseUrl) => connection.BaseUrl.StartsWith(baseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        public static async Task<SourceCatalogState> ReadAsync(SqlContext.SqlReadCommands sql, CancellationToken ct)
        {
            var connections = new SqlStatement();
            connections.Statement =
                """
                SELECT
                    connection."AcquisitionConnectionTypeId",
                    connection."BaseUrl"
                FROM
                    "AcquisitionConnections" AS connection;
                """;
            var providers = new SqlStatement();
            providers.Statement =
                """
                SELECT
                    provider."Key",
                    provider."IsEnabled"
                FROM
                    "Providers" AS provider;
                """;
            return new SourceCatalogState(await sql.ReadListAsync<SourceConnectionData>(connections, ct), await sql.ReadListAsync<SourceProviderData>(providers, ct));
        }
    }
}
