using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Session;

namespace Jularr.Backend.Logic.Playback
{
    internal static class CompanionLogic
    {
        public static async Task<(string Token, DateTime ExpiresAt)> CreateCompanionInvitationAsync(this LogicContext ctx, CompanionOwnerData owner, CancellationToken ct)
        {
            var playback = await ctx.LockCompanionOwnerAsync(owner, ct);
            var token = SessionLogic.NewToken();
            var expiresAt = DateTime.UtcNow.AddMinutes(5);
            var secret = new CompanionTokenHashData(SHA256.HashData(Encoding.UTF8.GetBytes(token)), expiresAt);

            var state = new SqlStatement();
            state.Statement =
                """
                INSERT INTO
                    "PlaybackCompanionStates" ("PlaybackSessionId")
                VALUES
                    (@Id)
                ON CONFLICT ("PlaybackSessionId") DO NOTHING;
                """;
            state.Parameters.Add(playback);
            await ctx.ExecuteAsyncForAffectedRows(state, ct);

            var invite = new SqlStatement();
            invite.Statement =
                """
                INSERT INTO
                    "PlaybackCompanionInvitations" ("PlaybackSessionId", "TokenHash", "ExpiresAt")
                VALUES
                    (@Id, @TokenHash, @ExpiresAt)
                ON CONFLICT ("PlaybackSessionId") DO UPDATE
                SET
                    "TokenHash" = EXCLUDED."TokenHash",
                    "ExpiresAt" = EXCLUDED."ExpiresAt",
                    "ClaimedAt" = NULL;
                """;
            invite.Parameters.Add(playback);
            invite.Parameters.Add(secret);
            await ctx.ExecuteAsyncForAffectedRows(invite, ct);
            return (token, expiresAt);
        }

        public static async Task<CompanionGrantRow> ClaimCompanionAsync(this LogicContext ctx, CompanionClaimData claim, CancellationToken ct)
        {
            // A successful claim consumes the unpredictable invitation in the same transaction.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH claimed AS (
                    UPDATE
                        "PlaybackCompanionInvitations" AS invitation
                    SET
                        "ClaimedAt" = statement_timestamp()
                    FROM
                        "PlaybackSessions" AS playback
                        JOIN "AccountSessions" AS owner ON owner."Id" = playback."OwnerSessionId"
                        JOIN "Accounts" AS account ON account."Id" = owner."AccountId"
                        JOIN "AccountSessions" AS controller ON controller."AccountId" = owner."AccountId"
                    WHERE
                        invitation."PlaybackSessionId" = playback."Id"
                        AND invitation."TokenHash" = @TokenHash
                        AND invitation."ClaimedAt" IS NULL
                        AND invitation."ExpiresAt" > statement_timestamp()
                        AND playback."EndedAt" IS NULL
                        AND playback."OwnerSessionRevision" = owner."RotationRevision"
                        AND playback."ProfileId" = owner."ActiveProfileId"
                        AND owner."DevicePlatform" = 'AndroidTV'
                        AND owner."RevokedAt" IS NULL
                        AND owner."ExpiresAt" > statement_timestamp()
                        AND account."IsEnabled"
                        AND account."CredentialRevision" = owner."CredentialRevision"
                        AND controller."PublicId" = @ControllerPublicId
                        AND controller."RotationRevision" = @ControllerRevision
                        AND controller."Id" <> owner."Id"
                        AND controller."ActiveProfileId" = @ProfileId
                        AND playback."ProfileId" = @ProfileId
                        AND controller."AccountId" = @AccountId
                        AND controller."RevokedAt" IS NULL
                        AND controller."ExpiresAt" > statement_timestamp()
                        AND controller."CredentialRevision" = account."CredentialRevision"
                    RETURNING
                        invitation."PlaybackSessionId"
                )
                INSERT INTO
                    "PlaybackCompanionGrants" AS access ("Id", "PlaybackSessionId", "ControllerSessionId", "ControllerSessionRevision")
                SELECT
                    @GrantId,
                    claimed."PlaybackSessionId",
                    controller."Id",
                    @ControllerRevision
                FROM
                    claimed
                    JOIN "AccountSessions" AS controller ON controller."PublicId" = @ControllerPublicId
                ON CONFLICT ("PlaybackSessionId", "ControllerSessionId") DO UPDATE
                SET
                    "ControllerSessionRevision" = EXCLUDED."ControllerSessionRevision",
                    "RevokedAt" = NULL
                RETURNING
                    access."Id",
                    (SELECT playback."PublicId" FROM "PlaybackSessions" AS playback WHERE playback."Id" = access."PlaybackSessionId") AS "PlaybackSessionId";
                """;
            sql.Parameters.Add(claim);
            sql.Parameters.Add(new CompanionGrantIdData(Guid.NewGuid()));
            return await ctx.ReadOptionalAsync<CompanionGrantRow>(sql, ct)
                ?? throw new KeyNotFoundException("The companion invitation is unavailable.");
        }

        public static async Task<CompanionStatusRow> ReadCompanionStateAsync(this LogicContext ctx, CompanionControllerData controller, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    playback."PublicId" AS "PlaybackSessionId",
                    state."Revision",
                    state."IsPlaying",
                    state."PositionMs",
                    state."DurationMs",
                    state."ReportedAt",
                    state."LastCommandId",
                    state."LastCommandSucceeded"
                FROM
                    "PlaybackCompanionGrants" AS access
                    JOIN "PlaybackSessions" AS playback ON playback."Id" = access."PlaybackSessionId"
                    JOIN "PlaybackCompanionStates" AS state ON state."PlaybackSessionId" = playback."Id"
                    JOIN "AccountSessions" AS owner ON owner."Id" = playback."OwnerSessionId"
                    JOIN "AccountSessions" AS actor ON actor."Id" = access."ControllerSessionId"
                    JOIN "Accounts" AS account ON account."Id" = owner."AccountId"
                WHERE
                    access."Id" = @CompanionId
                    AND access."RevokedAt" IS NULL
                    AND access."ControllerSessionRevision" = actor."RotationRevision"
                    AND actor."PublicId" = @ControllerPublicId
                    AND actor."RotationRevision" = @ControllerRevision
                    AND actor."AccountId" = @AccountId
                    AND actor."ActiveProfileId" = @ProfileId
                    AND actor."RevokedAt" IS NULL
                    AND actor."ExpiresAt" > statement_timestamp()
                    AND playback."ProfileId" = @ProfileId
                    AND playback."EndedAt" IS NULL
                    AND playback."OwnerSessionRevision" = owner."RotationRevision"
                    AND owner."ActiveProfileId" = playback."ProfileId"
                    AND owner."AccountId" = actor."AccountId"
                    AND owner."DevicePlatform" = 'AndroidTV'
                    AND owner."RevokedAt" IS NULL
                    AND owner."ExpiresAt" > statement_timestamp()
                    AND account."IsEnabled"
                    AND account."CredentialRevision" = actor."CredentialRevision"
                    AND account."CredentialRevision" = owner."CredentialRevision";
                """;
            sql.Parameters.Add(controller);
            return await ctx.ReadSql.ReadRequiredAsync<CompanionStatusRow>(sql, ct);
        }

        public static async Task<CompanionStatusRow> ReadCompanionOwnerStateAsync(this LogicContext ctx, CompanionOwnerData owner, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    playback."PublicId" AS "PlaybackSessionId",
                    state."Revision",
                    state."IsPlaying",
                    state."PositionMs",
                    state."DurationMs",
                    state."ReportedAt",
                    state."LastCommandId",
                    state."LastCommandSucceeded"
                FROM
                    "PlaybackSessions" AS playback
                    JOIN "PlaybackCompanionStates" AS state ON state."PlaybackSessionId" = playback."Id"
                    JOIN "AccountSessions" AS owner ON owner."Id" = playback."OwnerSessionId"
                    JOIN "Accounts" AS account ON account."Id" = owner."AccountId"
                WHERE
                    playback."PublicId" = @PlaybackSessionId
                    AND playback."ProfileId" = @ProfileId
                    AND playback."EndedAt" IS NULL
                    AND playback."OwnerSessionRevision" = owner."RotationRevision"
                    AND owner."PublicId" = @OwnerPublicId
                    AND owner."RotationRevision" = @OwnerRevision
                    AND owner."DevicePlatform" = 'AndroidTV'
                    AND owner."ActiveProfileId" = playback."ProfileId"
                    AND owner."RevokedAt" IS NULL
                    AND owner."ExpiresAt" > statement_timestamp()
                    AND account."IsEnabled"
                    AND account."CredentialRevision" = owner."CredentialRevision";
                """;
            sql.Parameters.Add(owner);
            return await ctx.ReadSql.ReadRequiredAsync<CompanionStatusRow>(sql, ct);
        }

        public static async Task<long> QueueCompanionCommandAsync(this LogicContext ctx, CompanionControllerData controller, CompanionCommandData command, CancellationToken ct)
        {
            var grant = await ctx.LockCompanionControllerAsync(controller, ct);

            // Repeated requests with the same command identity return the first revision; no duplicate playback action is created.
            var existing = new SqlStatement();
            existing.Statement =
                """
                SELECT
                    command."Revision"
                FROM
                    "PlaybackCompanionCommands" AS command
                WHERE
                    command."Id" = @CommandId
                    AND command."PlaybackSessionId" = @Id
                    AND command."ControllerGrantId" = @CompanionId;
                """;
            existing.Parameters.Add(grant);
            existing.Parameters.Add(controller);
            existing.Parameters.Add(command);
            if (await ctx.ReadOptionalAsync<CompanionRevisionRow>(existing, ct) is { } repeated)
            {
                return repeated.Revision;
            }

            // One outstanding command, one ordered revision. A silent TV cannot accumulate stale control actions.
            var update = new SqlStatement();
            update.Statement =
                """
                UPDATE
                    "PlaybackCompanionStates" AS state
                SET
                    "Revision" = state."Revision" + 1
                WHERE
                    state."PlaybackSessionId" = @Id
                    AND state."Revision" = @ExpectedRevision
                    AND state."ReportedAt" > statement_timestamp() - interval '15 seconds'
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "PlaybackCompanionCommands" AS pending
                        WHERE
                            pending."PlaybackSessionId" = state."PlaybackSessionId"
                            AND pending."FinishedAt" IS NULL
                            AND pending."ExpiresAt" > statement_timestamp()
                    )
                RETURNING
                    state."Revision";
                """;
            update.Parameters.Add(grant);
            update.Parameters.Add(command);
            var next = await ctx.ReadOptionalAsync<CompanionRevisionRow>(update, ct)
                ?? throw new DataValidationException(nameof(command.ExpectedRevision), "invalid_transition");

            var insert = new SqlStatement();
            insert.Statement =
                """
                INSERT INTO
                    "PlaybackCompanionCommands" ("Id", "PlaybackSessionId", "ControllerGrantId", "Revision", "Action", "PositionMs")
                VALUES
                    (@CommandId, @Id, @CompanionId, @Revision, @Action, @PositionMs);
                """;
            insert.Parameters.Add(command);
            insert.Parameters.Add(controller);
            insert.Parameters.Add(grant);
            insert.Parameters.Add(next);
            await ctx.ExecuteAsyncForAffectedRows(insert, ct);
            return next.Revision;
        }

        public static async Task<CompanionNextCommandRow?> TakeNextCompanionCommandAsync(this LogicContext ctx, CompanionOwnerData owner, CancellationToken ct)
        {
            var playback = await ctx.LockCompanionOwnerAsync(owner, ct);
            // Delivered commands are never dispatched a second time, even after a lost HTTP response.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH next AS (
                    SELECT
                        command."Id"
                    FROM
                        "PlaybackCompanionCommands" AS command
                        JOIN "PlaybackCompanionGrants" AS access ON access."Id" = command."ControllerGrantId"
                    WHERE
                        command."PlaybackSessionId" = @Id
                        AND command."DeliveredAt" IS NULL
                        AND command."FinishedAt" IS NULL
                        AND command."ExpiresAt" > statement_timestamp()
                        AND access."RevokedAt" IS NULL
                    ORDER BY
                        command."Revision"
                    LIMIT 1
                    FOR UPDATE OF command SKIP LOCKED
                )
                UPDATE
                    "PlaybackCompanionCommands" AS command
                SET
                    "DeliveredAt" = statement_timestamp()
                FROM
                    next
                WHERE
                    command."Id" = next."Id"
                RETURNING
                    command."Id",
                    command."Revision",
                    command."Action",
                    command."PositionMs";
                """;
            sql.Parameters.Add(playback);
            return await ctx.ReadOptionalAsync<CompanionNextCommandRow>(sql, ct);
        }

        public static async Task ReportCompanionStateAsync(this LogicContext ctx, CompanionOwnerData owner, CompanionStateData state, CancellationToken ct)
        {
            var playback = await ctx.LockCompanionOwnerAsync(owner, ct);
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "PlaybackCompanionStates" AS current
                SET
                    "IsPlaying" = @IsPlaying,
                    "PositionMs" = @PositionMs,
                    "DurationMs" = @DurationMs,
                    "ReportedAt" = statement_timestamp()
                WHERE
                    current."PlaybackSessionId" = @Id
                    AND current."Revision" = @Revision
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "PlaybackCompanionCommands" AS pending
                        WHERE
                            pending."PlaybackSessionId" = current."PlaybackSessionId"
                            AND pending."Revision" = current."Revision"
                            AND pending."DeliveredAt" IS NOT NULL
                            AND pending."FinishedAt" IS NULL
                            AND pending."ExpiresAt" > statement_timestamp()
                    );
                """;
            sql.Parameters.Add(playback);
            sql.Parameters.Add(state);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(state.Revision), "invalid_transition");
            }
        }

        public static async Task AcknowledgeCompanionCommandAsync(this LogicContext ctx, CompanionOwnerData owner, CompanionAckData result, CancellationToken ct)
        {
            var playback = await ctx.LockCompanionOwnerAsync(owner, ct);

            var command = new SqlStatement();
            command.Statement =
                """
                UPDATE
                    "PlaybackCompanionCommands" AS command
                SET
                    "FinishedAt" = statement_timestamp(),
                    "Succeeded" = @Succeeded
                FROM
                    "PlaybackCompanionGrants" AS access
                WHERE
                    command."Id" = @CommandId
                    AND command."Revision" = @Revision
                    AND command."PlaybackSessionId" = @Id
                    AND command."ControllerGrantId" = access."Id"
                    AND access."RevokedAt" IS NULL
                    AND command."DeliveredAt" IS NOT NULL
                    AND command."FinishedAt" IS NULL
                    AND command."ExpiresAt" > statement_timestamp();
                """;
            command.Parameters.Add(playback);
            command.Parameters.Add(result);
            if (await ctx.ExecuteAsyncForAffectedRows(command, ct) != 1)
            {
                throw new KeyNotFoundException("The command was already answered, revoked or expired.");
            }

            var state = new SqlStatement();
            state.Statement =
                """
                UPDATE
                    "PlaybackCompanionStates" AS current
                SET
                    "IsPlaying" = @IsPlaying,
                    "PositionMs" = @PositionMs,
                    "DurationMs" = @DurationMs,
                    "ReportedAt" = statement_timestamp(),
                    "LastCommandId" = @CommandId,
                    "LastCommandSucceeded" = @Succeeded
                WHERE
                    current."PlaybackSessionId" = @Id
                    AND current."Revision" = @Revision;
                """;
            state.Parameters.Add(playback);
            state.Parameters.Add(result);
            if (await ctx.ExecuteAsyncForAffectedRows(state, ct) != 1)
            {
                throw new DataValidationException(nameof(result.Revision), "invalid_transition");
            }
        }

        public static async Task RevokeCompanionAsync(this LogicContext ctx, CompanionControllerData controller, CancellationToken ct)
        {
            await ctx.LockCompanionControllerAsync(controller, ct);
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "PlaybackCompanionGrants" AS access
                SET
                    "RevokedAt" = statement_timestamp()
                WHERE
                    access."Id" = @CompanionId
                    AND access."RevokedAt" IS NULL;
                """;
            sql.Parameters.Add(controller);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The companion is unavailable.");
            }
        }

        public static async Task RevokePlaybackCompanionsAsync(this LogicContext ctx, CompanionOwnerData owner, CancellationToken ct)
        {
            var playback = await ctx.LockCompanionOwnerAsync(owner, ct);
            var invite = new SqlStatement();
            invite.Statement =
                """
                DELETE FROM
                    "PlaybackCompanionInvitations"
                WHERE
                    "PlaybackSessionId" = @Id;
                """;
            invite.Parameters.Add(playback);
            await ctx.ExecuteAsyncForAffectedRows(invite, ct);

            var grant = new SqlStatement();
            grant.Statement =
                """
                UPDATE
                    "PlaybackCompanionGrants"
                SET
                    "RevokedAt" = statement_timestamp()
                WHERE
                    "PlaybackSessionId" = @Id
                    AND "RevokedAt" IS NULL;
                """;
            grant.Parameters.Add(playback);
            await ctx.ExecuteAsyncForAffectedRows(grant, ct);
        }

        private static async Task<CompanionAccessRow> LockCompanionOwnerAsync(this LogicContext ctx, CompanionOwnerData owner, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    playback."Id",
                    playback."PublicId" AS "PlaybackSessionId"
                FROM
                    "PlaybackSessions" AS playback
                    JOIN "AccountSessions" AS owner ON owner."Id" = playback."OwnerSessionId"
                    JOIN "Accounts" AS account ON account."Id" = owner."AccountId"
                WHERE
                    playback."PublicId" = @PlaybackSessionId
                    AND playback."ProfileId" = @ProfileId
                    AND playback."EndedAt" IS NULL
                    AND playback."OwnerSessionRevision" = owner."RotationRevision"
                    AND owner."PublicId" = @OwnerPublicId
                    AND owner."RotationRevision" = @OwnerRevision
                    AND owner."ActiveProfileId" = playback."ProfileId"
                    AND owner."DevicePlatform" = 'AndroidTV'
                    AND owner."RevokedAt" IS NULL
                    AND owner."ExpiresAt" > statement_timestamp()
                    AND account."IsEnabled"
                    AND account."CredentialRevision" = owner."CredentialRevision"
                FOR UPDATE OF
                    playback, owner;
                """;
            sql.Parameters.Add(owner);
            return await ctx.ReadOptionalAsync<CompanionAccessRow>(sql, ct)
                ?? throw new KeyNotFoundException("The TV playback session is unavailable.");
        }

        private static async Task<CompanionAccessRow> LockCompanionControllerAsync(this LogicContext ctx, CompanionControllerData controller, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    playback."Id",
                    playback."PublicId" AS "PlaybackSessionId"
                FROM
                    "PlaybackCompanionGrants" AS access
                    JOIN "PlaybackSessions" AS playback ON playback."Id" = access."PlaybackSessionId"
                    JOIN "AccountSessions" AS actor ON actor."Id" = access."ControllerSessionId"
                    JOIN "AccountSessions" AS owner ON owner."Id" = playback."OwnerSessionId"
                    JOIN "Accounts" AS account ON account."Id" = owner."AccountId"
                WHERE
                    access."Id" = @CompanionId
                    AND access."RevokedAt" IS NULL
                    AND access."ControllerSessionRevision" = actor."RotationRevision"
                    AND actor."PublicId" = @ControllerPublicId
                    AND actor."RotationRevision" = @ControllerRevision
                    AND actor."AccountId" = @AccountId
                    AND actor."ActiveProfileId" = @ProfileId
                    AND actor."RevokedAt" IS NULL
                    AND actor."ExpiresAt" > statement_timestamp()
                    AND playback."ProfileId" = @ProfileId
                    AND playback."EndedAt" IS NULL
                    AND playback."OwnerSessionRevision" = owner."RotationRevision"
                    AND owner."ActiveProfileId" = playback."ProfileId"
                    AND owner."AccountId" = actor."AccountId"
                    AND owner."DevicePlatform" = 'AndroidTV'
                    AND owner."RevokedAt" IS NULL
                    AND owner."ExpiresAt" > statement_timestamp()
                    AND account."IsEnabled"
                    AND account."CredentialRevision" = actor."CredentialRevision"
                    AND account."CredentialRevision" = owner."CredentialRevision"
                FOR UPDATE OF
                    access, owner, actor;
                """;
            sql.Parameters.Add(controller);
            return await ctx.ReadOptionalAsync<CompanionAccessRow>(sql, ct)
                ?? throw new KeyNotFoundException("The companion permission is unavailable.");
        }
    }
}
