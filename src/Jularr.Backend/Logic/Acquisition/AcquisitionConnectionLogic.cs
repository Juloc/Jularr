using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Storage;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class AcquisitionConnectionLogic
    {
        public static Task<AcquisitionConnectionData> ReadAcquisitionConnectionAsync(this LogicContext ctx, long acquisitionConnectionId, CancellationToken ct)
        {
            // Connection facts for external work.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    connection."Id",
                    connection."Name",
                    connection."AcquisitionConnectionTypeId",
                    connection."BaseUrl",
                    connection."Host",
                    connection."Port",
                    connection."UseSsl",
                    connection."ProtectedApiKey",
                    connection."IsEnabled",
                    connection."Priority",
                    connection."Categories",
                    connection."DownloadCategory",
                    connection."RemotePath",
                    connection."LocalPath",
                    connection."DownloadStorageMountId",
                    connection."Username",
                    connection."MaxConnections",
                    connection."Capabilities"::text AS "Capabilities",
                    connection."Version",
                    connection."ProtectedPassword",
                    connection."MediaTypeIds",
                    connection."SearchesAutomatically",
                    connection."QueryLimit",
                    connection."GrabLimit",
                    CASE WHEN connection."CountedOn" = current_date THEN connection."QueryCount" ELSE 0 END AS "QueryCount",
                    CASE WHEN connection."CountedOn" = current_date THEN connection."GrabCount" ELSE 0 END AS "GrabCount",
                    connection."RetryAt"
                FROM
                    "AcquisitionConnections" AS connection
                WHERE
                    connection."Id" = @AcquisitionConnectionId;
                """;
            sql.Parameters.Add(new AcquisitionConnectionParameters(acquisitionConnectionId));
            return ctx.ReadSql.ReadRequiredAsync<AcquisitionConnectionData>(sql, ct);
        }

        // The enabled connections of one role, best priority first.
        public static Task<IReadOnlyList<AcquisitionConnectionData>> ReadEnabledConnectionsAsync(this LogicContext ctx, AcquisitionConnectionRole role, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    connection."Id",
                    connection."Name",
                    connection."AcquisitionConnectionTypeId",
                    connection."BaseUrl",
                    connection."Host",
                    connection."Port",
                    connection."UseSsl",
                    connection."ProtectedApiKey",
                    connection."IsEnabled",
                    connection."Priority",
                    connection."Categories",
                    connection."DownloadCategory",
                    connection."RemotePath",
                    connection."LocalPath",
                    connection."DownloadStorageMountId",
                    connection."Username",
                    connection."MaxConnections",
                    connection."Capabilities"::text AS "Capabilities",
                    connection."Version",
                    connection."ProtectedPassword",
                    connection."MediaTypeIds",
                    connection."SearchesAutomatically",
                    connection."QueryLimit",
                    connection."GrabLimit",
                    CASE WHEN connection."CountedOn" = current_date THEN connection."QueryCount" ELSE 0 END AS "QueryCount",
                    CASE WHEN connection."CountedOn" = current_date THEN connection."GrabCount" ELSE 0 END AS "GrabCount",
                    connection."RetryAt"
                FROM
                    "AcquisitionConnections" AS connection
                    JOIN "AcquisitionConnectionTypes" AS type ON type."Id" = connection."AcquisitionConnectionTypeId"
                WHERE
                    connection."IsEnabled"
                    AND type."AcquisitionConnectionRoleId" = @AcquisitionConnectionRoleId
                ORDER BY
                    connection."Priority",
                    connection."Id";
                """;
            sql.Parameters.Add(new AcquisitionConnectionRoleParameters(role));
            return ctx.ReadSql.ReadListAsync<AcquisitionConnectionData>(sql, ct);
        }

        // An indexer serves its media types, and automatic searches only when it is not manual-only.
        public static bool Serves(AcquisitionConnectionData indexer, MediaType mediaType, bool automatic) =>
            (indexer.MediaTypeIds.Length == 0 || indexer.MediaTypeIds.Contains((int)mediaType)) && (!automatic || indexer.SearchesAutomatically);

        // Queries left today after the ones a search already used.
        public static bool HasQueries(AcquisitionConnectionData indexer, int used) => indexer.QueryLimit is null || indexer.QueryCount + used < indexer.QueryLimit;

        // Automatic searches also skip backed-off indexers and those without grabs left.
        public static bool CanSearch(AcquisitionConnectionData indexer, MediaType mediaType, bool automatic, DateTime now) =>
            Serves(indexer, mediaType, automatic) && HasQueries(indexer, 0)
            && (!automatic || (indexer.RetryAt is null || indexer.RetryAt <= now) && (indexer.GrabLimit is null || indexer.GrabCount < indexer.GrabLimit));

        // Today's queries count against the limit; a failure backs off from 5 minutes doubling up to a day, an answer ends the backoff.
        public static async Task RecordIndexerUseAsync(this LogicContext ctx, IndexerUseData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AcquisitionConnections" AS connection
                SET
                    "QueryCount" = CASE WHEN connection."CountedOn" = current_date THEN connection."QueryCount" ELSE 0 END + @QueryCount,
                    "GrabCount" = CASE WHEN connection."CountedOn" = current_date THEN connection."GrabCount" ELSE 0 END,
                    "CountedOn" = current_date,
                    "FailureCount" = CASE WHEN @IsAvailable THEN 0 ELSE connection."FailureCount" + 1 END,
                    "RetryAt" = CASE WHEN NOT @IsAvailable THEN now() + least(interval '5 minutes' * power(2, least(connection."FailureCount", 9)), interval '1 day') END,
                    "LastErrorKey" = CASE WHEN NOT @IsAvailable THEN 'acquisition_unavailable' END,
                    "CheckedAt" = now()
                WHERE
                    connection."Id" = @AcquisitionConnectionId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // A grab counts against the indexer's daily limit; false once it is reached. A removed indexer's offer is stale.
        public static async Task<bool> CountIndexerGrabAsync(this LogicContext ctx, AcquisitionConnectionParameters data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AcquisitionConnections" AS connection
                SET
                    "QueryCount" = CASE WHEN connection."CountedOn" = current_date THEN connection."QueryCount" ELSE 0 END,
                    "GrabCount" = CASE WHEN connection."CountedOn" = current_date THEN connection."GrabCount" ELSE 0 END + 1,
                    "CountedOn" = current_date
                WHERE
                    connection."Id" = @AcquisitionConnectionId
                    AND (connection."GrabLimit" IS NULL OR connection."CountedOn" IS DISTINCT FROM current_date OR connection."GrabCount" < connection."GrabLimit");
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) > 0)
            {
                return true;
            }

            var exists = new SqlStatement();
            exists.Statement =
                """
                SELECT
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "AcquisitionConnections" AS connection
                        WHERE
                            connection."Id" = @AcquisitionConnectionId
                    );
                """;
            exists.Parameters.Add(data);
            return (bool)(await ctx.ReadSql.ExecuteScalarAsync(exists, ct))! ? false : throw new DataValidationException("CandidateId", "indexer_removed");
        }

        public static async Task ReturnIndexerGrabAsync(this LogicContext ctx, AcquisitionConnectionParameters data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AcquisitionConnections" AS connection
                SET
                    "GrabCount" = connection."GrabCount" - 1
                WHERE
                    connection."Id" = @AcquisitionConnectionId
                    AND connection."CountedOn" = current_date
                    AND connection."GrabCount" > 0;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // A news server is a host with SSL and an optional port; everything else an HTTP address. The Anna's Archive member key travels in the address and only over HTTPS.
        private static void ValidateAddress(AcquisitionConnectionSaveData data)
        {
            if (data.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet)
            {
                if (data.Host is null)
                {
                    throw new DataValidationException(nameof(data.Host), "required");
                }

                if (data.BaseUrl is not null || data.UseSsl is null)
                {
                    throw new DataValidationException(nameof(data.BaseUrl), "invalid_transition");
                }

                return;
            }

            if (data.BaseUrl is not { } baseUrl)
            {
                throw new DataValidationException(nameof(data.BaseUrl), "required");
            }

            if (data.Host is not null || data.Port is not null || data.UseSsl is not null)
            {
                throw new DataValidationException(nameof(data.Host), "invalid_transition");
            }

            var secure = data.AcquisitionConnectionTypeId is AcquisitionConnectionType.AnnasArchive or AcquisitionConnectionType.OpenSubtitles or AcquisitionConnectionType.SubDl;
            if (secure && new Uri(baseUrl).Scheme != "https")
            {
                throw new DataValidationException(nameof(data.BaseUrl), "invalid_url");
            }
        }

        // Download clients and subtitle providers need a key; indexers, catalogs and news servers may be open.
        private static bool NeedsKey(AcquisitionConnectionType type) => type is AcquisitionConnectionType.Sabnzbd or AcquisitionConnectionType.OpenSubtitles or AcquisitionConnectionType.SubDl;

        private static void ValidateDownloadMapping(AcquisitionConnectionSaveData data)
        {
            if (data.DownloadStorageMountId is not null)
            {
                try
                {
                    StoragePath.Check(data.LocalPath ?? "");
                }
                catch (NasMountException)
                {
                    throw new DataValidationException(nameof(data.LocalPath), "invalid_path");
                }
            }
        }

        public static async Task<long> CreateAcquisitionConnectionAsync(this LogicContext ctx, AcquisitionConnectionSaveData data, CancellationToken ct)
        {
            ValidateAddress(data);
            ValidateDownloadMapping(data);
            if (data.DownloadStorageMountId is { } mountId) await ctx.ReadStorageMountAsync(mountId, ct);

            if (data.ProtectedApiKey is null && NeedsKey(data.AcquisitionConnectionTypeId))
            {
                throw new DataValidationException("ApiKey", "required");
            }

            // Store protected configuration.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AcquisitionConnections" ("Name", "AcquisitionConnectionTypeId", "BaseUrl", "Host", "Port", "UseSsl", "ProtectedApiKey", "IsEnabled", "Priority", "Categories",
                        "DownloadCategory", "RemotePath", "LocalPath", "DownloadStorageMountId", "Username", "MaxConnections", "ProtectedPassword", "MediaTypeIds", "SearchesAutomatically", "QueryLimit", "GrabLimit")
                VALUES
                    (@Name, @AcquisitionConnectionTypeId, @BaseUrl, @Host, @Port, @UseSsl, @ProtectedApiKey, @IsEnabled, @Priority, @Categories, @DownloadCategory, @RemotePath,
                        @LocalPath, @DownloadStorageMountId, @Username, @MaxConnections, @ProtectedPassword, @MediaTypeIds, @SearchesAutomatically, @QueryLimit, @GrabLimit)
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadRequiredAsync<AcquisitionConnectionIdData>(sql, ct)).Id;
        }

        public static async Task UpdateAcquisitionConnectionAsync(this LogicContext ctx, AcquisitionConnectionSaveData data, CancellationToken ct)
        {
            ValidateAddress(data);
            ValidateDownloadMapping(data);
            if (data.DownloadStorageMountId is { } mountId) await ctx.ReadStorageMountAsync(mountId, ct);

            // A stored key stays only with its type.
            if (data.ProtectedApiKey is null && NeedsKey(data.AcquisitionConnectionTypeId)
                && await ctx.ReadAcquisitionConnectionAsync(data.Id!.Value, ct) is var stored && (stored.AcquisitionConnectionTypeId != data.AcquisitionConnectionTypeId || stored.ProtectedApiKey is null))
            {
                throw new DataValidationException("ApiKey", "required");
            }

            // Preserve an omitted API key of the same type; a new address or type needs a new check.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AcquisitionConnections" AS connection
                SET
                    "Name" = @Name,
                    "AcquisitionConnectionTypeId" = @AcquisitionConnectionTypeId,
                    "BaseUrl" = @BaseUrl,
                    "Host" = @Host,
                    "Port" = @Port,
                    "UseSsl" = @UseSsl,
                    "ProtectedApiKey" = CASE
                        WHEN connection."AcquisitionConnectionTypeId" = @AcquisitionConnectionTypeId THEN COALESCE(@ProtectedApiKey, connection."ProtectedApiKey")
                        ELSE @ProtectedApiKey
                    END,
                    "IsEnabled" = @IsEnabled,
                    "Priority" = @Priority,
                    "Categories" = @Categories,
                    "DownloadCategory" = @DownloadCategory,
                    "RemotePath" = @RemotePath,
                    "LocalPath" = @LocalPath,
                    "DownloadStorageMountId" = @DownloadStorageMountId,
                    "Username" = @Username,
                    "MaxConnections" = @MaxConnections,
                    "MediaTypeIds" = @MediaTypeIds,
                    "SearchesAutomatically" = @SearchesAutomatically,
                    "QueryLimit" = @QueryLimit,
                    "GrabLimit" = @GrabLimit,
                    "FailureCount" = 0,
                    "RetryAt" = NULL,
                    "ProtectedPassword" = CASE WHEN @AcquisitionConnectionTypeId = 7 THEN COALESCE(@ProtectedPassword, connection."ProtectedPassword") END,
                    "Capabilities" = CASE
                        WHEN connection."BaseUrl" IS NOT DISTINCT FROM @BaseUrl AND connection."Host" IS NOT DISTINCT FROM @Host
                            AND connection."Port" IS NOT DISTINCT FROM @Port AND connection."UseSsl" IS NOT DISTINCT FROM @UseSsl
                            AND connection."AcquisitionConnectionTypeId" = @AcquisitionConnectionTypeId AND @ProtectedApiKey IS NULL
                            THEN connection."Capabilities"
                    END,
                    "Version" = connection."Version" + 1,
                    "UpdatedAt" = statement_timestamp()
                WHERE
                    connection."Id" = @Id;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The acquisition connection was not found.");
            }
        }

        public static async Task SaveAcquisitionConnectionCheckAsync(this LogicContext ctx, AcquisitionConnectionCheckData data, CancellationToken ct)
        {
            // A failed check keeps the last known capabilities; an edited connection ignores the old result.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AcquisitionConnections" AS connection
                SET
                    "Capabilities" = COALESCE(@Capabilities::jsonb, connection."Capabilities"),
                    "CheckedAt" = statement_timestamp(),
                    "LastErrorKey" = @ErrorKey,
                    "FailureCount" = CASE WHEN @ErrorKey IS NULL THEN 0 ELSE connection."FailureCount" END,
                    "RetryAt" = CASE WHEN @ErrorKey IS NULL THEN NULL ELSE connection."RetryAt" END
                WHERE
                    connection."Id" = @Id
                    AND connection."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task DeleteAcquisitionConnectionAsync(this LogicContext ctx, AcquisitionConnectionParameters data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    connection."Version"
                FROM
                    "AcquisitionConnections" AS connection
                WHERE
                    connection."Id" = @AcquisitionConnectionId
                FOR UPDATE;
                """;
            sql.Parameters.Add(data);
            var current = await ctx.ReadOptionalAsync<AcquisitionConnectionVersionData>(sql, ct) ?? throw new KeyNotFoundException("The acquisition connection was not found.");
            if (current.Version != data.Version)
            {
                throw new DataValidationException(nameof(AcquisitionConnectionParameters.Version), "outdated_version");
            }

            // Running downloads still need their client.
            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "AcquisitionConnections" AS connection
                WHERE
                    connection."Id" = @AcquisitionConnectionId
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "Operations" AS operation
                        WHERE
                            operation."AcquisitionConnectionId" = connection."Id"
                            AND operation."OperationStatusTypeId" IN (1, 2)
                    );
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(AcquisitionConnectionParameters.AcquisitionConnectionId), "in_use");
            }
        }
    }
}
