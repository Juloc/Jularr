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
        public static Task<AcquisitionConnectionData> ReadAcquisitionConnectionAsync(this LogicContext ctx, long acquisitionConnectionId, CancellationToken ct) =>
            ctx.ReadSql.ReadRequiredAsync<AcquisitionConnectionData>(ReadAcquisitionConnectionStatement(acquisitionConnectionId), ct);

        internal static SqlStatement ReadAcquisitionConnectionStatement(long acquisitionConnectionId)
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
                    connection."RemovesCompleted",
                    connection."RemovesFailed",
                    connection."RemotePath",
                    connection."LocalPath",
                    connection."IncompleteRemotePath",
                    connection."IncompleteLocalPath",
                    connection."DownloadStorageMountId",
                    connection."Username",
                    connection."MaxConnections",
                    connection."Capabilities"::text AS "Capabilities",
                    connection."Version",
                    connection."ProtectedPassword",
                    connection."MediaTypeIds",
                    connection."SearchesAutomatically",
                    connection."ReadsFeed",
                    connection."SearchesInteractively",
                    connection."SearchIntervalHours",
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
            return sql;
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
                    connection."RemovesCompleted",
                    connection."RemovesFailed",
                    connection."RemotePath",
                    connection."LocalPath",
                    connection."IncompleteRemotePath",
                    connection."IncompleteLocalPath",
                    connection."DownloadStorageMountId",
                    connection."Username",
                    connection."MaxConnections",
                    connection."Capabilities"::text AS "Capabilities",
                    connection."Version",
                    connection."ProtectedPassword",
                    connection."MediaTypeIds",
                    connection."SearchesAutomatically",
                    connection."ReadsFeed",
                    connection."SearchesInteractively",
                    connection."SearchIntervalHours",
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

        // A source serves its media types; as Sonarr's indexer flags, an automatic search asks only the sources that search automatically, a person's search only the interactive ones.
        public static bool Serves(AcquisitionConnectionData indexer, MediaType mediaType, bool automatic) =>
            (indexer.MediaTypeIds.Length == 0 || indexer.MediaTypeIds.Contains((int)mediaType)) && (automatic ? indexer.SearchesAutomatically : indexer.SearchesInteractively);

        // The media types an indexer's recent feed (RSS) is read and judged for.
        public static readonly MediaType[] FeedMediaTypes = [MediaType.Movie, MediaType.Series, MediaType.Book, MediaType.Music];

        // A Newznab indexer with its feed on covers what it serves of those types: its new releases are judged and grabbed as they come, so it is never searched on a rhythm.
        public static bool HasFeed(AcquisitionConnectionData indexer, MediaType mediaType) =>
            indexer.AcquisitionConnectionTypeId == AcquisitionConnectionType.Newznab && indexer.ReadsFeed && FeedMediaTypes.Contains(mediaType)
            && (indexer.MediaTypeIds.Length == 0 || indexer.MediaTypeIds.Contains((int)mediaType));

        // A feed is read within the indexer's daily limits and outside its backoff.
        public static bool CanReadFeed(AcquisitionConnectionData indexer, DateTime now) =>
            HasQueries(indexer, 0) && (indexer.RetryAt is null || indexer.RetryAt <= now) && (indexer.GrabLimit is null || indexer.GrabCount < indexer.GrabLimit);

        // A source without a feed searches a wanted title on its own rhythm: once in every period of its interval, at a moment the title's number places within the period,
        // so the titles spread over it. Returns whether such a moment lies after the last search and not after now.
        public static bool IsPeriodicDue(AcquisitionConnectionData source, long wantedItemId, DateTime? lastSearchedAt, DateTime now) =>
            lastSearchedAt is not { } last || NextPeriodicSearch(source, wantedItemId, last) <= now;

        // The first moment of the source's rhythm for this title after the given time.
        public static DateTime NextPeriodicSearch(AcquisitionConnectionData source, long wantedItemId, DateTime after)
        {
            var period = TimeSpan.FromHours(source.SearchIntervalHours).Ticks;
            var offset = (wantedItemId * 7919 % (source.SearchIntervalHours * 60L) + source.SearchIntervalHours * 60L) % (source.SearchIntervalHours * 60L) * TimeSpan.TicksPerMinute;
            var ticks = after.Ticks - offset;
            return new DateTime((ticks / period + 1) * period + offset, DateTimeKind.Utc);
        }

        // Queries left today after the ones a search already used; only a limit the Owner set applies.
        public static bool HasQueries(AcquisitionConnectionData indexer, int used) => indexer.QueryLimit is not { } limit || indexer.QueryCount + used < limit;

        // Automatic searches also skip backed-off indexers and those without grabs left.
        public static bool CanSearch(AcquisitionConnectionData indexer, MediaType mediaType, bool automatic, DateTime now) =>
            Serves(indexer, mediaType, automatic) && HasQueries(indexer, 0)
            && (!automatic || (indexer.RetryAt is null || indexer.RetryAt <= now) && (indexer.GrabLimit is null || indexer.GrabCount < indexer.GrabLimit));

        // Today's queries count against the limit; a failure backs off 1, 5, 15, 30, 60 minutes, then 3, 6, 12 and at most 24 hours, or for the wait the indexer asked for if that is longer; an answer ends the backoff.
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
                    "RetryAt" = CASE WHEN NOT @IsAvailable THEN now() + greatest(interval '1 minute' * (ARRAY[1, 5, 15, 30, 60, 180, 360, 720, 1440])[least(connection."FailureCount", 8) + 1], interval '1 second' * COALESCE(@RetryAfterSeconds, 0)) END,
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
            // Jularr's own torrent engine runs here and has no address.
            if (data.AcquisitionConnectionTypeId == AcquisitionConnectionType.Torrent)
            {
                if (data.BaseUrl is not null || data.Host is not null || data.Port is not null || data.UseSsl is not null)
                {
                    throw new DataValidationException(nameof(data.BaseUrl), "invalid_transition");
                }

                return;
            }

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

            // Narō is asked at its own hosts only; the address names the novel site.
            if (data.AcquisitionConnectionTypeId == AcquisitionConnectionType.Narou && new Uri(baseUrl) is not { Scheme: "https", Host: "ncode.syosetu.com", AbsolutePath: "/" })
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
                        "DownloadCategory", "RemovesCompleted", "RemovesFailed", "RemotePath", "LocalPath", "IncompleteRemotePath", "IncompleteLocalPath", "DownloadStorageMountId", "Username", "MaxConnections", "ProtectedPassword", "MediaTypeIds", "SearchesAutomatically", "QueryLimit", "GrabLimit", "ReadsFeed", "SearchesInteractively", "SearchIntervalHours")
                VALUES
                    (@Name, @AcquisitionConnectionTypeId, @BaseUrl, @Host, @Port, @UseSsl, @ProtectedApiKey, @IsEnabled, @Priority, @Categories, @DownloadCategory, @RemovesCompleted, @RemovesFailed, @RemotePath,
                        @LocalPath, @IncompleteRemotePath, @IncompleteLocalPath, @DownloadStorageMountId, @Username, @MaxConnections, @ProtectedPassword, @MediaTypeIds, @SearchesAutomatically, @QueryLimit, @GrabLimit, @ReadsFeed, @SearchesInteractively, @SearchIntervalHours)
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
                    "RemovesCompleted" = @RemovesCompleted,
                    "RemovesFailed" = @RemovesFailed,
                    "RemotePath" = @RemotePath,
                    "LocalPath" = @LocalPath,
                    "IncompleteRemotePath" = @IncompleteRemotePath,
                    "IncompleteLocalPath" = @IncompleteLocalPath,
                    "DownloadStorageMountId" = @DownloadStorageMountId,
                    "Username" = @Username,
                    "MaxConnections" = @MaxConnections,
                    "MediaTypeIds" = @MediaTypeIds,
                    "SearchesAutomatically" = @SearchesAutomatically,
                    "ReadsFeed" = @ReadsFeed,
                    "SearchesInteractively" = @SearchesInteractively,
                    "SearchIntervalHours" = @SearchIntervalHours,
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
