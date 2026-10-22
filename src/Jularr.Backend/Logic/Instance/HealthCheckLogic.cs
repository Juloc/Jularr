using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Instance
{
    // What the health check lists and how it judges each service: only what is configured and switched on appears, and every judgment follows from a stored or just measured answer.
    internal static class HealthCheckLogic
    {
        // The metadata providers Jularr asks itself, as their keys in Providers and the names they go by.
        public static readonly IReadOnlyDictionary<string, string> ProviderNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tmdb"] = "TMDB",
            ["tvdb"] = "TheTVDB",
            ["anilist"] = "AniList",
            ["openlibrary"] = "Open Library",
            ["musicbrainz"] = "MusicBrainz",
            ["igdb"] = "IGDB",
            ["hardcover"] = "Hardcover",
            ["acoustid"] = "AcoustID"
        };

        // An indexer's or news server's test is repeated only when its last one is older than this; a download client is asked every time.
        public static readonly TimeSpan StoredTestLasts = TimeSpan.FromMinutes(30);

        // The stored configuration the check starts from.
        public static async Task<HealthTargets> ReadHealthTargetsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var modules = await ctx.ReadModuleStatesAsync(new InstanceModuleParameters(["Acquisition", "ProgressSync", "Library"]), ct);

            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    provider."Key" AS "ProviderKey",
                    provider."IsEnabled",
                    provider."ProtectedSecret" IS NOT NULL AS "HasSecret"
                FROM
                    "Providers" AS provider
                WHERE
                    provider."Key" = ANY(@ProviderKeys)
                ORDER BY
                    provider."Key";
                """;
            sql.Parameters.Add(new HealthProviderKeysData([.. ProviderNames.Keys]));
            var providers = await ctx.ReadSql.ReadListAsync<HealthProviderData>(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    mount."Id" AS "StorageMountId",
                    mount."Name" AS "MountName",
                    health."StorageHealthTypeId",
                    health."DiagnosticCode",
                    health."CheckedAt"
                FROM
                    "StorageMounts" AS mount
                    JOIN "StorageMountHealth" AS health ON health."StorageMountId" = mount."Id"
                ORDER BY
                    mount."Name",
                    mount."Id";
                """;
            var mounts = await ctx.ReadSql.ReadListAsync<HealthMountData>(sql, ct);

            // Indexers, download clients and news servers only while Arr+ is on.
            IReadOnlyList<HealthConnectionData> connections = [];
            if (modules.Any(module => module.Key == "Acquisition" && module.IsEnabled))
            {
                sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        connection."Id",
                        connection."Name" AS "ConnectionName",
                        connection."AcquisitionConnectionTypeId" AS "ConnectionTypeId",
                        connection."Version",
                        connection."CheckedAt",
                        connection."LastErrorKey"
                    FROM
                        "AcquisitionConnections" AS connection
                    WHERE
                        connection."IsEnabled"
                        AND connection."AcquisitionConnectionTypeId" IN (1, 2, 6)
                    ORDER BY
                        connection."Name",
                        connection."Id";
                    """;
                connections = await ctx.ReadSql.ReadListAsync<HealthConnectionData>(sql, ct);
            }

            return new HealthTargets([.. modules.Where(module => module.IsEnabled).Select(module => module.Key)], providers, mounts, connections);
        }

        public static HealthServiceV1 Database(DateTime now) => new(HealthServiceType.Database, "database", "PostgreSQL", HealthStateType.Available, null, now);

        // The storage worker keeps the state; a sleeping connection that Jularr can wake is not a problem.
        public static HealthServiceV1 Mount(HealthMountData mount)
        {
            var state = mount.StorageHealthTypeId switch
            {
                StorageHealthType.Online or StorageHealthType.Sleeping => HealthStateType.Available,
                StorageHealthType.Offline or StorageHealthType.Error => HealthStateType.Unavailable,
                _ => HealthStateType.Unknown
            };
            var errorKey = state == HealthStateType.Unavailable ? mount.DiagnosticCode is "permission_denied" or "probe_timeout" ? mount.DiagnosticCode : "storage_unavailable" : null;
            return new HealthServiceV1(HealthServiceType.Storage, "mount:" + mount.StorageMountId, mount.MountName, state, errorKey, mount.CheckedAt);
        }

        // A provider shows once something has asked it, or once its key is stored; what the last request said is all that is known without asking again.
        public static HealthServiceV1? Provider(HealthProviderData provider, ProviderObservation? last)
        {
            if (!provider.IsEnabled || last is null && !provider.HasSecret)
            {
                return null;
            }

            var state = last is null ? HealthStateType.Unknown : last.Succeeded ? HealthStateType.Available : HealthStateType.Unavailable;
            return new HealthServiceV1(HealthServiceType.Provider, "provider:" + provider.ProviderKey, ProviderNames[provider.ProviderKey], state, state == HealthStateType.Unavailable ? "provider_unavailable" : null, last?.At);
        }

        // A client is asked on every check; an indexer or news server keeps its last test for a while.
        public static bool NeedsTest(HealthConnectionData connection, DateTime now) =>
            connection.ConnectionTypeId == AcquisitionConnectionType.Sabnzbd || connection.CheckedAt is not { } at || now - at >= StoredTestLasts;

        // An indexer is an indexer; the download client and the news server it downloads from are download clients.
        public static HealthServiceType ServiceType(HealthConnectionData connection) =>
            connection.ConnectionTypeId == AcquisitionConnectionType.Newznab ? HealthServiceType.Indexer : HealthServiceType.DownloadClient;

        // A stored test says nothing until the connection was tested once.
        public static HealthServiceV1 StoredConnection(HealthConnectionData connection) => new(ServiceType(connection), "connection:" + connection.Id, connection.ConnectionName,
            connection.CheckedAt is null ? HealthStateType.Unknown : connection.LastErrorKey is null ? HealthStateType.Available : HealthStateType.Unavailable, connection.LastErrorKey, connection.CheckedAt);

        // Stable unless one service did not answer; a service nobody asked yet is no failure.
        public static HealthV1 Summarize(IEnumerable<HealthServiceV1> services, DateTime? checkedAt)
        {
            var ordered = services.OrderBy(service => service.HealthServiceTypeId).ThenBy(service => service.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return new HealthV1(ordered.All(service => service.HealthStateTypeId != HealthStateType.Unavailable), checkedAt, new BaseDataList<HealthServiceV1>(ordered));
        }
    }

    // The configuration one check works through.
    internal sealed record HealthTargets(IReadOnlyCollection<string> EnabledModules, IReadOnlyList<HealthProviderData> Providers, IReadOnlyList<HealthMountData> Mounts,
        IReadOnlyList<HealthConnectionData> Connections);
}
