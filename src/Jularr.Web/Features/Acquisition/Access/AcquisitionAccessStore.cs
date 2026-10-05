using System.Data;
using System.Data.Common;
using System.Globalization;
using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>A payload patch lost the compare-and-set race on every attempt (see AcquisitionAccessStore.PatchPayloadAsync).</summary>
public sealed class PayloadConflictException() : InvalidOperationException("The request payload kept changing; try again.");

/// <summary>Another open request for the same title exists already (unique index on the open title).</summary>
public sealed class OpenRequestExistsException(Exception inner) : Exception("The title already has an open request.", inner);

/// <summary>Persistence of the access policies and acquisition requests (tables from migration 20260927120000).</summary>
public sealed class AcquisitionAccessStore(AppDbContext db)
{
    private const string OpenTitleIndex = "IX_AcquisitionRequests_OpenTitle";
    private const string OpenStatuses = "'pending', 'approved', 'searching', 'downloading', 'importing'";

    private const int MaxPatchAttempts = 8;

    private sealed record StatusChange(AcquisitionRequestStatus Expected, Func<string?, AcquisitionStatusOutcome> Choose);

    private const string Columns =
        """
        "Id", "Kind", "Provider", "ExternalId", "Title", "Subtitle", "CoverImageUrl", "PayloadJson",
        "RequestedByProfileId", "Status", "StatusMessage", "OperationId", "ResultUrl",
        "CreatedAt", "UpdatedAt", "DecidedByProfileId", "DecidedAt"
        """;

    public async Task<IReadOnlyList<AcquisitionAccessPolicy>> GetPoliciesAsync(CancellationToken cancellationToken)
    {
        var stored = await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """SELECT "Kind", "ManualAddMode" FROM "AcquisitionAccessPolicies";""";
            var rows = new Dictionary<MediaAcquisitionKind, AcquisitionAccessPolicy>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var kind = AcquisitionAccessNames.ParseKind(reader.GetString(0));
                rows[kind] = new AcquisitionAccessPolicy(kind, AcquisitionAccessNames.ParseManual(reader.GetString(1)));
            }

            return rows;
        }, cancellationToken);

        return Enum.GetValues<MediaAcquisitionKind>()
            .Select(kind => stored.GetValueOrDefault(kind) ?? AcquisitionAccessPolicy.Default(kind))
            .ToArray();
    }

    public async Task<AcquisitionAccessPolicy> GetPolicyAsync(MediaAcquisitionKind kind, CancellationToken cancellationToken) =>
        (await GetPoliciesAsync(cancellationToken)).Single(policy => policy.Kind == kind);

    /// <summary>
    /// Saves the manual add rule of one media type. Whether a profile may request or add instantly comes
    /// from the capability matrix (#436), not this table; the retired <c>UserAddMode</c> column was dropped
    /// by the Movie/TV schema migration (#593/#594), so only the manual rule is persisted here.
    /// </summary>
    public Task SavePolicyAsync(AcquisitionAccessPolicy policy, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "AcquisitionAccessPolicies" ("Kind", "ManualAddMode", "UpdatedAt")
                VALUES (@kind, @manual, @now)
                ON CONFLICT("Kind") DO UPDATE SET
                    "ManualAddMode" = excluded."ManualAddMode",
                    "UpdatedAt" = excluded."UpdatedAt";
                """;
            Add(command, "@kind", AcquisitionAccessNames.Kind(policy.Kind));
            Add(command, "@manual", AcquisitionAccessNames.Manual(policy.Manual));
            Add(command, "@now", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>The open request for the same title, if any — a title is only ever requested once at a time.</summary>
    public Task<AcquisitionRequest?> FindOpenAsync(
        MediaAcquisitionKind kind,
        string provider,
        string externalId,
        CancellationToken cancellationToken) =>
        QuerySingleAsync(
            $"""
            SELECT {Columns} FROM "AcquisitionRequests"
            WHERE "Kind" = @kind AND "Provider" = @provider AND "ExternalId" = @externalId
              AND "Status" IN ('pending', 'approved', 'searching', 'downloading', 'importing')
            LIMIT 1;
            """,
            command =>
            {
                Add(command, "@kind", AcquisitionAccessNames.Kind(kind));
                Add(command, "@provider", provider);
                Add(command, "@externalId", externalId);
            },
            cancellationToken);

    public Task<AcquisitionRequest?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        QuerySingleAsync(
            $"""SELECT {Columns} FROM "AcquisitionRequests" WHERE "Id" = @id LIMIT 1;""",
            command => Add(command, "@id", id.ToString()),
            cancellationToken);

    /// <summary>The request whose current download is this operation, if any.</summary>
    public Task<AcquisitionRequest?> FindByOperationAsync(Guid operationId, CancellationToken cancellationToken) =>
        QuerySingleAsync(
            $"""SELECT {Columns} FROM "AcquisitionRequests" WHERE "OperationId" = @operationId ORDER BY "UpdatedAt" DESC LIMIT 1;""",
            command => Add(command, "@operationId", operationId.ToString()),
            cancellationToken);

    public Task<IReadOnlyList<AcquisitionRequest>> ListAsync(
        MediaAcquisitionKind? kind,
        string? requestedByProfileId,
        bool openOnly,
        int limit,
        CancellationToken cancellationToken) =>
        QueryAsync(
            $"""
            SELECT {Columns} FROM "AcquisitionRequests"
            WHERE (@kind::text IS NULL OR "Kind" = @kind)
              AND (@profile::text IS NULL OR "RequestedByProfileId" = @profile)
              AND (@openOnly = 0 OR "Status" IN ('pending', 'approved', 'searching', 'downloading', 'importing'))
            ORDER BY CASE "Status" WHEN 'pending' THEN 0 ELSE 1 END, "UpdatedAt" DESC
            LIMIT @limit;
            """,
            command =>
            {
                Add(command, "@kind", kind is { } value ? AcquisitionAccessNames.Kind(value) : null);
                Add(command, "@profile", requestedByProfileId);
                Add(command, "@openOnly", openOnly ? 1 : 0);
                Add(command, "@limit", Math.Clamp(limit, 1, 500));
            },
            cancellationToken);

    /// <summary>Every request, waiting ones first, then most recently changed first; the owner's queue narrows them.</summary>
    public Task<IReadOnlyList<AcquisitionRequest>> ListAllAsync(int limit, CancellationToken cancellationToken) =>
        QueryAsync(
            $"""
            SELECT {Columns} FROM "AcquisitionRequests"
            ORDER BY CASE "Status" WHEN 'pending' THEN 0 ELSE 1 END, "UpdatedAt" DESC, "Id"
            LIMIT @limit;
            """,
            command => Add(command, "@limit", Math.Clamp(limit, 1, 5000)),
            cancellationToken);

    /// <summary>One page of a profile's own requests, most recently changed first.</summary>
    public Task<IReadOnlyList<AcquisitionRequest>> ListForProfileAsync(
        string requestedByProfileId,
        RequestHistoryFilter filter,
        int offset,
        int limit,
        CancellationToken cancellationToken) =>
        QueryAsync(
            $"""
            SELECT {Columns} FROM "AcquisitionRequests"
            WHERE "RequestedByProfileId" = @profile
              AND (@filter = 'all'
                   OR (@filter = 'open' AND "Status" IN ({OpenStatuses}))
                   OR (@filter = 'finished' AND "Status" NOT IN ({OpenStatuses})))
            ORDER BY "UpdatedAt" DESC, "Id"
            LIMIT @limit OFFSET @offset;
            """,
            command =>
            {
                Add(command, "@profile", requestedByProfileId);
                Add(command, "@filter", filter switch
                {
                    RequestHistoryFilter.Open => "open",
                    RequestHistoryFilter.Finished => "finished",
                    _ => "all"
                });
                Add(command, "@limit", Math.Clamp(limit, 1, 200));
                Add(command, "@offset", Math.Max(offset, 0));
            },
            cancellationToken);

    /// <summary>How many requests of a profile are still open and how many are finished.</summary>
    public async Task<(int Open, int Finished)> CountForProfileAsync(
        string requestedByProfileId,
        CancellationToken cancellationToken) =>
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT COUNT(*) FILTER (WHERE "Status" IN ({OpenStatuses})),
                       COUNT(*) FILTER (WHERE "Status" NOT IN ({OpenStatuses}))
                FROM "AcquisitionRequests" WHERE "RequestedByProfileId" = @profile;
                """;
            Add(command, "@profile", requestedByProfileId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return (
                Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture));
        }, cancellationToken);

    /// <summary>How many of a profile's requests one auto-approval rule approved since <paramref name="sinceUtc"/>.</summary>
    public async Task<int> CountAutoApprovedSinceAsync(
        string requestedByProfileId,
        string ruleId,
        DateTime sinceUtc,
        CancellationToken cancellationToken) =>
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*) FROM "AcquisitionRequests"
                WHERE "RequestedByProfileId" = @profile AND "DecidedByProfileId" = @decidedBy AND "CreatedAt" >= @since;
                """;
            Add(command, "@profile", requestedByProfileId);
            Add(command, "@decidedBy", AcquisitionAutoApproval.DecidedBy(ruleId));
            Add(command, "@since", sinceUtc);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }, cancellationToken);

    public Task<IReadOnlyList<AcquisitionRequest>> ListDownloadingAsync(
        MediaAcquisitionKind kind,
        CancellationToken cancellationToken) =>
        QueryAsync(
            $"""
            SELECT {Columns} FROM "AcquisitionRequests"
            WHERE "Kind" = @kind AND "Status" = 'downloading';
            """,
            command => Add(command, "@kind", AcquisitionAccessNames.Kind(kind)),
            cancellationToken);

    public Task<IReadOnlyList<AcquisitionRequest>> ListByStatusAsync(
        MediaAcquisitionKind kind,
        AcquisitionRequestStatus status,
        CancellationToken cancellationToken) =>
        QueryAsync(
            $"""
            SELECT {Columns} FROM "AcquisitionRequests"
            WHERE "Kind" = @kind AND "Status" = @status
            ORDER BY "UpdatedAt";
            """,
            command =>
            {
                Add(command, "@kind", AcquisitionAccessNames.Kind(kind));
                Add(command, "@status", AcquisitionAccessNames.Status(status));
            },
            cancellationToken);

    /// <summary>
    /// One batch of the requests of a media type that a pass reads back from its monitoring pipeline (see
    /// <see cref="AcquisitionRequest.IsObservedFromMonitoring"/>), in id order after <paramref name="afterId"/>. Walking the batches by the
    /// last id reaches every request however many there are, and a request that is not written meanwhile keeps its place.
    /// </summary>
    public Task<IReadOnlyList<AcquisitionRequest>> ListObservedFromMonitoringAsync(
        MediaAcquisitionKind kind,
        Guid? afterId,
        int limit,
        CancellationToken cancellationToken) =>
        QueryAsync(
            $"""
            SELECT {Columns} FROM "AcquisitionRequests"
            WHERE "Kind" = @kind
              AND ("Status" = ANY(@underway) OR ("Status" = 'failed' AND "OperationId" IS NOT NULL))
              AND (@after::text IS NULL OR "Id" > @after)
            ORDER BY "Id"
            LIMIT @limit;
            """,
            command =>
            {
                Add(command, "@kind", AcquisitionAccessNames.Kind(kind));
                Add(command, "@after", afterId?.ToString());
                Add(command, "@limit", limit);
                var underway = command.CreateParameter();
                underway.ParameterName = "@underway";
                underway.Value = AcquisitionAccessNames.UnderwayStatuses.Select(AcquisitionAccessNames.Status).ToArray();
                command.Parameters.Add(underway);
            },
            cancellationToken);

    /// <summary>Replaces the media-specific payload (for example which releases were already tried).</summary>
    public Task UpdatePayloadAsync(Guid id, string? payloadJson, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """UPDATE "AcquisitionRequests" SET "PayloadJson" = @payload WHERE "Id" = @id;""";
            Add(command, "@id", id.ToString());
            Add(command, "@payload", payloadJson);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>
    /// Changes the payload from what is stored now: <paramref name="patch"/> gets the current text and returns the new one, and the
    /// write only lands if nothing else changed the payload meanwhile (compare and set), otherwise the patch runs again on the newer
    /// text. Two owners of different payload fields therefore never overwrite each other. Returns false when the request is gone.
    /// </summary>
    public Task<bool> PatchPayloadAsync(Guid id, Func<string?, string?> patch, CancellationToken cancellationToken) =>
        PatchAsync(id, patch, null, cancellationToken);

    /// <summary>
    /// <see cref="PatchPayloadAsync(Guid, Func{string?, string?}, CancellationToken)"/> and a status change in one write: the status only
    /// moves from <paramref name="expected"/> to <paramref name="status"/> together with the payload, so the two can never disagree.
    /// Returns false when the request is gone, is no longer in <paramref name="expected"/>, or the new status would open a second request
    /// for the title; nothing is written then. A null <paramref name="message"/> keeps the message, and the request only counts as changed
    /// (<c>UpdatedAt</c>) when its status really changes, so a patch does not restart the stale-search clock of a claimed request.
    /// </summary>
    public Task<bool> PatchPayloadAsync(Guid id, Func<string?, string?> patch, AcquisitionRequestStatus expected, AcquisitionRequestStatus status, string? message, CancellationToken cancellationToken) =>
        PatchAsync(id, patch, new StatusChange(expected, _ => new AcquisitionStatusOutcome(status, message)), cancellationToken);

    /// <summary>
    /// The same write where the new status is chosen from the payload stored at that moment (and the write is conditional on that very
    /// payload), for a result that is only true while the payload still says what the run read.
    /// </summary>
    public Task<bool> PatchPayloadAsync(Guid id, Func<string?, string?> patch, AcquisitionRequestStatus expected, Func<string?, AcquisitionStatusOutcome> choose, CancellationToken cancellationToken) =>
        PatchAsync(id, patch, new StatusChange(expected, choose), cancellationToken);

    private Task<bool> PatchAsync(Guid id, Func<string?, string?> patch, StatusChange? change, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            for (var attempt = 0; attempt < MaxPatchAttempts; attempt++)
            {
                string? current;
                await using (var read = connection.CreateCommand())
                {
                    read.CommandText = """SELECT "PayloadJson", "Status" FROM "AcquisitionRequests" WHERE "Id" = @id;""";
                    Add(read, "@id", id.ToString());
                    await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                    if (!await reader.ReadAsync(cancellationToken) || change is not null && reader.GetString(1) != AcquisitionAccessNames.Status(change.Expected))
                    {
                        return false;
                    }

                    current = reader.IsDBNull(0) ? null : reader.GetString(0);
                }

                await using var write = connection.CreateCommand();
                write.CommandText = change is null
                    ? """UPDATE "AcquisitionRequests" SET "PayloadJson" = @next WHERE "Id" = @id AND "PayloadJson" IS NOT DISTINCT FROM @current::text;"""
                    : """
                      UPDATE "AcquisitionRequests"
                      SET "PayloadJson" = @next, "Status" = @status, "StatusMessage" = COALESCE(@message, "StatusMessage"),
                          "ResultUrl" = COALESCE(@resultUrl, "ResultUrl"), "UpdatedAt" = CASE WHEN "Status" = @status THEN "UpdatedAt" ELSE @now END
                      WHERE "Id" = @id AND "PayloadJson" IS NOT DISTINCT FROM @current::text AND "Status" = @expected;
                      """;
                Add(write, "@id", id.ToString());
                Add(write, "@next", patch(current));
                Add(write, "@current", current);
                if (change is not null)
                {
                    var outcome = change.Choose(current);
                    Add(write, "@expected", AcquisitionAccessNames.Status(change.Expected));
                    Add(write, "@status", AcquisitionAccessNames.Status(outcome.Status));
                    Add(write, "@message", outcome.Message);
                    Add(write, "@resultUrl", outcome.ResultUrl);
                    Add(write, "@now", DateTime.UtcNow);
                }

                try
                {
                    if (await write.ExecuteNonQueryAsync(cancellationToken) == 1)
                    {
                        return true;
                    }
                }
                catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation && exception.ConstraintName == OpenTitleIndex)
                {
                    return false;
                }
            }

            throw new PayloadConflictException();
        }, cancellationToken);

    /// <summary>The newest request for the title in any state, or null; an open request is the newest one.</summary>
    public Task<AcquisitionRequest?> FindLatestAsync(MediaAcquisitionKind kind, string provider, string externalId, CancellationToken cancellationToken) =>
        QuerySingleAsync(
            $"""
            SELECT {Columns} FROM "AcquisitionRequests"
            WHERE "Kind" = @kind AND "Provider" = @provider AND "ExternalId" = @externalId
            ORDER BY "CreatedAt" DESC
            LIMIT 1;
            """,
            command =>
            {
                Add(command, "@kind", AcquisitionAccessNames.Kind(kind));
                Add(command, "@provider", provider);
                Add(command, "@externalId", externalId);
            },
            cancellationToken);

    public async Task<int> CountPendingAsync(CancellationToken cancellationToken) =>
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """SELECT COUNT(*) FROM "AcquisitionRequests" WHERE "Status" = 'pending';""";
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }, cancellationToken);

    public async Task<AcquisitionRequest> CreateAsync(
        AcquisitionRequestDraft draft,
        string requestedByProfileId,
        AcquisitionRequestStatus status,
        string? decidedByProfileId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var request = new AcquisitionRequest(
            Guid.NewGuid(),
            draft.Kind,
            draft.Provider,
            draft.ExternalId,
            draft.Title,
            draft.Subtitle,
            draft.CoverImageUrl,
            draft.PayloadJson,
            requestedByProfileId,
            status,
            null,
            null,
            null,
            now,
            now,
            decidedByProfileId,
            decidedByProfileId is null ? null : now);

        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                INSERT INTO "AcquisitionRequests" ({Columns})
                VALUES (@id, @kind, @provider, @externalId, @title, @subtitle, @cover, @payload,
                        @requestedBy, @status, NULL, NULL, NULL, @now, @now, @decidedBy, @decidedAt);
                """;
            Add(command, "@id", request.Id.ToString());
            Add(command, "@kind", AcquisitionAccessNames.Kind(request.Kind));
            Add(command, "@provider", request.Provider);
            Add(command, "@externalId", request.ExternalId);
            Add(command, "@title", request.Title);
            Add(command, "@subtitle", request.Subtitle);
            Add(command, "@cover", request.CoverImageUrl);
            Add(command, "@payload", request.PayloadJson);
            Add(command, "@requestedBy", request.RequestedByProfileId);
            Add(command, "@status", AcquisitionAccessNames.Status(request.Status));
            Add(command, "@now", now);
            Add(command, "@decidedBy", decidedByProfileId);
            Add(command, "@decidedAt", request.DecidedAt);
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation && exception.ConstraintName == OpenTitleIndex)
            {
                throw new OpenRequestExistsException(exception);
            }

            return true;
        }, cancellationToken);

        return request;
    }

    public Task UpdateStatusAsync(
        Guid id,
        AcquisitionRequestStatus status,
        string? message,
        Guid? operationId,
        string? resultUrl,
        string? decidedByProfileId,
        CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE "AcquisitionRequests"
                SET "Status" = @status,
                    "StatusMessage" = @message,
                    "OperationId" = COALESCE(@operationId, "OperationId"),
                    "ResultUrl" = COALESCE(@resultUrl, "ResultUrl"),
                    "DecidedByProfileId" = COALESCE(@decidedBy, "DecidedByProfileId"),
                    "DecidedAt" = CASE WHEN @decidedBy::text IS NULL THEN "DecidedAt" ELSE @now END,
                    "UpdatedAt" = @now
                WHERE "Id" = @id;
                """;
            Add(command, "@id", id.ToString());
            Add(command, "@status", AcquisitionAccessNames.Status(status));
            Add(command, "@message", message);
            Add(command, "@operationId", operationId?.ToString());
            Add(command, "@resultUrl", resultUrl);
            Add(command, "@decidedBy", decidedByProfileId);
            Add(command, "@now", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>
    /// Moves a request to <paramref name="status"/> only while it is still in one of <paramref name="expected"/>, in one statement, and
    /// returns what it was before. Null means it changed meanwhile (rejected, grabbed by the scheduler, cancelled), so the caller must stop
    /// instead of acting on a stale read. A given <paramref name="operationId"/> is linked to the request and a given
    /// <paramref name="resultUrl"/> replaces its result address.
    /// </summary>
    public Task<AcquisitionStatusTransition?> TryTransitionStatusAsync(
        Guid id,
        IReadOnlyCollection<AcquisitionRequestStatus> expected,
        AcquisitionRequestStatus status,
        string? message,
        Guid? operationId,
        CancellationToken cancellationToken) =>
        TryTransitionStatusAsync(id, expected, status, message, operationId, resultUrl: null, cancellationToken);

    /// <summary>The same transition that also replaces the result address of the request.</summary>
    public Task<AcquisitionStatusTransition?> TryTransitionStatusAsync(
        Guid id,
        IReadOnlyCollection<AcquisitionRequestStatus> expected,
        AcquisitionRequestStatus status,
        string? message,
        Guid? operationId,
        string? resultUrl,
        CancellationToken cancellationToken) =>
        TryTransitionStatusAsync(id, expected, status, message, operationId, resultUrl, clearOperation: false, cancellationToken);

    /// <summary>
    /// The same transition that, when <paramref name="clearOperation"/> is set, also unlinks the download the request had: a request that
    /// is back to waiting for a release must not point at a download that is no longer its own.
    /// </summary>
    public Task<AcquisitionStatusTransition?> TryTransitionStatusAsync(
        Guid id,
        IReadOnlyCollection<AcquisitionRequestStatus> expected,
        AcquisitionRequestStatus status,
        string? message,
        Guid? operationId,
        string? resultUrl,
        bool clearOperation,
        CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE "AcquisitionRequests" AS current
                SET "Status" = @status,
                    "StatusMessage" = @message,
                    "OperationId" = CASE WHEN @clearOperation THEN NULL ELSE COALESCE(@operationId, current."OperationId") END,
                    "ResultUrl" = COALESCE(@resultUrl, current."ResultUrl"),
                    "UpdatedAt" = @now
                FROM (SELECT "Status" AS "PreviousStatus", "StatusMessage" AS "PreviousMessage" FROM "AcquisitionRequests" WHERE "Id" = @id) AS previous
                WHERE current."Id" = @id AND current."Status" = ANY(@expected)
                RETURNING previous."PreviousStatus", previous."PreviousMessage";
                """;
            Add(command, "@id", id.ToString());
            Add(command, "@status", AcquisitionAccessNames.Status(status));
            Add(command, "@message", message);
            Add(command, "@operationId", operationId?.ToString());
            Add(command, "@resultUrl", resultUrl);
            Add(command, "@clearOperation", clearOperation);
            Add(command, "@now", DateTime.UtcNow);
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@expected";
            parameter.Value = expected.Select(AcquisitionAccessNames.Status).ToArray();
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? new AcquisitionStatusTransition(AcquisitionAccessNames.ParseStatus(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetString(1))
                : null;
        }, cancellationToken);

    private async Task<AcquisitionRequest?> QuerySingleAsync(
        string sql,
        Action<DbCommand> bind,
        CancellationToken cancellationToken) =>
        (await QueryAsync(sql, bind, cancellationToken)).FirstOrDefault();

    private Task<IReadOnlyList<AcquisitionRequest>> QueryAsync(
        string sql,
        Action<DbCommand> bind,
        CancellationToken cancellationToken) =>
        WithConnectionAsync<IReadOnlyList<AcquisitionRequest>>(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            bind(command);
            var rows = new List<AcquisitionRequest>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new AcquisitionRequest(
                    Guid.Parse(reader.GetString(0)),
                    AcquisitionAccessNames.ParseKind(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    NullableString(reader, 5),
                    NullableString(reader, 6),
                    NullableString(reader, 7),
                    reader.GetString(8),
                    AcquisitionAccessNames.ParseStatus(reader.GetString(9)),
                    NullableString(reader, 10),
                    NullableString(reader, 11) is { } operation ? Guid.Parse(operation) : null,
                    NullableString(reader, 12),
                    ParseDate(reader.GetString(13)),
                    ParseDate(reader.GetString(14)),
                    NullableString(reader, 15),
                    NullableString(reader, 16) is { } decided ? ParseDate(decided) : null));
            }

            return rows;
        }, cancellationToken);

    private async Task<T> WithConnectionAsync<T>(Func<DbConnection, Task<T>> action, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            return await action(connection);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static string? NullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTime ParseDate(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value switch
        {
            null => DBNull.Value,
            DateTime timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
            _ => value
        };
        command.Parameters.Add(parameter);
    }
}
