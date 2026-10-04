using System.Data;
using System.Data.Common;
using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Events;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Operations;

/// <summary>
/// The canonical status store for every download and import (#579): <see cref="MarkSucceededAsync"/>
/// and <see cref="MarkFailedAsync"/> are the one place a download or import operation finishes, so
/// they are also the chokepoint for the #429 <c>DownloadGrabbed</c>/<c>DownloadFailed</c> and
/// <c>ImportCompleted</c>/<c>ImportFailed</c> events. <paramref name="events"/> is optional and
/// defaults to null so the ~15 existing <c>new OperationStore(db)</c> call sites that do not care
/// about notifications keep compiling; callers that own a download or import path pass the real
/// publisher instead.
/// </summary>
public sealed class OperationStore(AppDbContext db, IJularrEventPublisher? events = null)
{
    private const int MaxTitleLength = 240;
    private const int MaxSubjectLength = 500;
    private const int MaxMessageLength = 2000;
    private const int MaxErrorLength = 3000;
    private const int MaxModuleLength = 120;
    private const int MaxExternalProviderLength = 80;
    private const int MaxExternalIdLength = 240;
    private const int MaxDetailsLength = 8000;
    private const int MaxActorLength = 80;

    public async Task<Guid> CreateAsync(
        OperationDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await WithConnectionAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO "Operations" (
                        "Id", "Kind", "Category", "Lane", "Status", "ProfileId", "Title", "Subject",
                        "ProgressPercent", "Message", "Error", "IsDownload", "BytesTotal",
                        "BytesCompleted", "BytesPerSecond", "EtaUtc", "Attempt", "Retryable",
                        "ExternalProvider", "ExternalId",
                        "CreatedAtUtc", "StartedAtUtc", "FinishedAtUtc", "UpdatedAtUtc", "Details",
                        "ActorProfileId", "Priority")
                    VALUES (
                        @id, @kind, @category, @lane, @status, @profileId, @title, @subject,
                        NULL, NULL, NULL, @isDownload, @bytesTotal,
                        NULL, NULL, NULL, 1, @retryable,
                        @externalProvider, @externalId,
                        @createdAt, NULL, NULL, @updatedAt, @details,
                        @actorProfileId, @priority);
                    """;
                Add(command, "@id", id.ToString("D"));
                Add(command, "@kind", Trim(descriptor.Kind, 100) ?? "background");
                Add(command, "@category", Trim(descriptor.Category, 80) ?? "Task");
                Add(command, "@lane", (int)descriptor.Lane);
                Add(command, "@status", (int)OperationStatus.Queued);
                Add(command, "@profileId", Trim(descriptor.ProfileId, 80));
                Add(command, "@title", Trim(descriptor.Title, MaxTitleLength) ?? "Background task");
                Add(command, "@subject", Trim(descriptor.Subject, MaxSubjectLength));
                Add(command, "@isDownload", descriptor.IsDownload ? 1 : 0);
                Add(command, "@bytesTotal", descriptor.BytesTotal);
                Add(command, "@retryable", descriptor.Retryable ? 1 : 0);
                Add(command, "@externalProvider", Trim(descriptor.ExternalProvider, MaxExternalProviderLength));
                Add(command, "@externalId", Trim(descriptor.ExternalId, MaxExternalIdLength));
                Add(command, "@createdAt", Format(now));
                Add(command, "@updatedAt", Format(now));
                Add(command, "@details", Trim(descriptor.Details, MaxDetailsLength));
                Add(command, "@actorProfileId", Trim(descriptor.ActorProfileId ?? OperationActor.Current, MaxActorLength));
                Add(command, "@priority", (int)descriptor.Priority);
                await command.ExecuteNonQueryAsync(cancellationToken);
            },
            cancellationToken);

        await AppendLogAsync(
            id,
            OperationLogLevel.Information,
            "Queue",
            "Operation queued.",
            cancellationToken);

        return id;
    }

    public async Task<OperationSnapshot?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        await WithConnectionAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT "Id", "Kind", "Category", "Lane", "Status", "ProfileId", "Title", "Subject",
                           "ProgressPercent", "Message", "Error", "IsDownload", "BytesTotal",
                           "BytesCompleted", "BytesPerSecond", "EtaUtc", "Attempt", "Retryable",
                           "ExternalProvider", "ExternalId",
                           "CreatedAtUtc", "StartedAtUtc", "FinishedAtUtc", "UpdatedAtUtc", "Details", "ActorProfileId", "Priority"
                    FROM "Operations"
                    WHERE "Id" = @id
                    LIMIT 1;
                    """;
                Add(command, "@id", id.ToString("D"));

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                return await reader.ReadAsync(cancellationToken)
                    ? ReadOperation(reader)
                    : null;
            },
            cancellationToken);

    public async Task<IReadOnlyList<OperationSnapshot>> ListAsync(
        OperationListFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var conditions = new List<string>();
        var parameters = new List<(string Name, object? Value)>();

        switch (filter.View?.Trim().ToLowerInvariant())
        {
            case "active":
                conditions.Add("\"Status\" IN (1, 2)");
                break;
            case "queue":
                conditions.Add("\"Status\" = 1");
                break;
            case "downloads":
                conditions.Add("\"IsDownload\" = 1");
                break;
            case "history":
                conditions.Add("\"Status\" IN (3, 4, 5, 6)");
                break;
        }

        if (filter.Status is { } status)
        {
            conditions.Add("\"Status\" = @status");
            parameters.Add(("@status", (int)status));
        }

        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            conditions.Add("\"Category\" = @category");
            parameters.Add(("@category", filter.Category.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(filter.Kind))
        {
            conditions.Add("\"Kind\" = @kind");
            parameters.Add(("@kind", filter.Kind.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            conditions.Add("(\"Title\" ILIKE @search OR \"Subject\" ILIKE @search OR \"Message\" ILIKE @search OR \"Kind\" ILIKE @search)");
            parameters.Add(("@search", $"%{filter.Search.Trim()}%"));
        }

        var where = conditions.Count == 0
            ? string.Empty
            : "WHERE " + string.Join(" AND ", conditions);

        var limit = Math.Clamp(filter.Limit, 1, 500);

        return await WithConnectionAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                    SELECT "Id", "Kind", "Category", "Lane", "Status", "ProfileId", "Title", "Subject",
                           "ProgressPercent", "Message", "Error", "IsDownload", "BytesTotal",
                           "BytesCompleted", "BytesPerSecond", "EtaUtc", "Attempt", "Retryable",
                           "ExternalProvider", "ExternalId",
                           "CreatedAtUtc", "StartedAtUtc", "FinishedAtUtc", "UpdatedAtUtc", "Details", "ActorProfileId", "Priority"
                    FROM "Operations"
                    {where}
                    ORDER BY
                        CASE "Status" WHEN 2 THEN 0 WHEN 1 THEN 1 ELSE 2 END,
                        "UpdatedAtUtc" DESC
                    LIMIT {limit};
                    """;

                foreach (var parameter in parameters)
                {
                    Add(command, parameter.Name, parameter.Value);
                }

                var result = new List<OperationSnapshot>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    result.Add(ReadOperation(reader));
                }

                return (IReadOnlyList<OperationSnapshot>)result;
            },
            cancellationToken);
    }

    /// <summary>
    /// One page of the finished operations (Admin → History), newest or oldest first by the time they
    /// finished, with how many operations the filter matches in total. The date range, result, kind and
    /// text filters all run in the database, so a long history stays cheap to page through.
    /// </summary>
    public async Task<OperationHistoryPage> QueryHistoryAsync(
        OperationHistoryFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Kinds is { Count: 0 })
        {
            return new OperationHistoryPage([], 0);
        }

        var (where, parameters) = HistoryWhere(filter, includeKinds: true);
        var direction = filter.NewestFirst ? "DESC" : "ASC";
        var limit = Math.Clamp(filter.Limit, 1, 200);
        var offset = Math.Max(filter.Offset, 0);

        return await WithConnectionAsync(
            async connection =>
            {
                int total;
                await using (var count = connection.CreateCommand())
                {
                    count.CommandText = $"""SELECT COUNT(*) FROM "Operations" {where};""";
                    foreach (var parameter in parameters)
                    {
                        Add(count, parameter.Name, parameter.Value);
                    }

                    total = Convert.ToInt32(
                        await count.ExecuteScalarAsync(cancellationToken),
                        CultureInfo.InvariantCulture);
                }

                var items = new List<OperationSnapshot>();
                if (total > 0)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        $"""
                        SELECT "Id", "Kind", "Category", "Lane", "Status", "ProfileId", "Title", "Subject",
                               "ProgressPercent", "Message", "Error", "IsDownload", "BytesTotal",
                               "BytesCompleted", "BytesPerSecond", "EtaUtc", "Attempt", "Retryable",
                               "ExternalProvider", "ExternalId",
                               "CreatedAtUtc", "StartedAtUtc", "FinishedAtUtc", "UpdatedAtUtc", "Details", "ActorProfileId", "Priority"
                        FROM "Operations"
                        {where}
                        ORDER BY {HistoryTime} {direction}, "Id" {direction}
                        LIMIT {limit} OFFSET {offset};
                        """;
                    foreach (var parameter in parameters)
                    {
                        Add(command, parameter.Name, parameter.Value);
                    }

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        items.Add(ReadOperation(reader));
                    }
                }

                return new OperationHistoryPage(items, total);
            },
            cancellationToken);
    }

    /// <summary>
    /// How many finished operations each kind has under the filter, ignoring the filter's kinds; the
    /// history page groups them into its categories for the counts and to narrow to one category.
    /// </summary>
    public async Task<IReadOnlyList<OperationKindCount>> CountHistoryByKindAsync(
        OperationHistoryFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var (where, parameters) = HistoryWhere(filter, includeKinds: false);

        return await WithConnectionAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                    SELECT "Kind", "Category", COUNT(*)
                    FROM "Operations"
                    {where}
                    GROUP BY "Kind", "Category";
                    """;
                foreach (var parameter in parameters)
                {
                    Add(command, parameter.Name, parameter.Value);
                }

                var rows = new List<OperationKindCount>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(new OperationKindCount(
                        new OperationKindKey(reader.GetString(0), reader.GetString(1)),
                        ReadCount(reader, 2)));
                }

                return (IReadOnlyList<OperationKindCount>)rows;
            },
            cancellationToken);
    }

    // The moment an operation entered history; a finished operation always has one, the fallback only
    // guards rows written by older versions.
    private const string HistoryTime = "COALESCE(\"FinishedAtUtc\", \"UpdatedAtUtc\")";

    private static (string Where, List<(string Name, object? Value)> Parameters) HistoryWhere(
        OperationHistoryFilter filter,
        bool includeKinds)
    {
        var conditions = new List<string> { "\"Status\" IN (3, 4, 5, 6)" };
        var parameters = new List<(string Name, object? Value)>();

        if (filter.FromUtc is { } from)
        {
            conditions.Add($"{HistoryTime} >= @fromUtc");
            parameters.Add(("@fromUtc", Format(from)));
        }

        if (filter.ToUtc is { } to)
        {
            conditions.Add($"{HistoryTime} < @toUtc");
            parameters.Add(("@toUtc", Format(to)));
        }

        if (filter.Statuses is { Count: > 0 } statuses)
        {
            conditions.Add($"\"Status\" IN ({string.Join(", ", statuses.Select(status => (int)status))})");
        }

        if (includeKinds && filter.Kinds is { Count: > 0 } kinds)
        {
            var alternatives = new List<string>();
            foreach (var key in kinds)
            {
                var index = parameters.Count;
                alternatives.Add($"(\"Kind\" = @kind{index} AND \"Category\" = @category{index})");
                parameters.Add(($"@kind{index}", key.Kind));
                parameters.Add(($"@category{index}", key.Category));
            }

            conditions.Add("(" + string.Join(" OR ", alternatives) + ")");
        }

        AddSearch(conditions, parameters, filter.Search, filter.SearchActorIds);

        return ("WHERE " + string.Join(" AND ", conditions), parameters);
    }

    // Free text over what an operation says about itself (and, in the history, the people who started it).
    private static void AddSearch(
        List<string> conditions,
        List<(string Name, object? Value)> parameters,
        string? text,
        IReadOnlyCollection<string>? actorIds)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var search = "%" + text.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";
        parameters.Add(("@search", search));
        var alternatives = new List<string>
        {
            "\"Title\" ILIKE @search",
            "\"Subject\" ILIKE @search",
            "\"Message\" ILIKE @search",
            "\"Error\" ILIKE @search",
            "\"Kind\" ILIKE @search"
        };
        if (actorIds is { Count: > 0 })
        {
            var names = new List<string>();
            foreach (var actor in actorIds)
            {
                var name = $"@actor{parameters.Count}";
                names.Add(name);
                parameters.Add((name, actor));
            }

            alternatives.Add($"\"ActorProfileId\" IN ({string.Join(", ", names)})");
        }

        conditions.Add("(" + string.Join(" OR ", alternatives) + ")");
    }

    /// <summary>
    /// One page of the work queue (Admin → Activity): operations in the given statuses and kinds that
    /// match the text, running first, then queued, failed and interrupted, then the rest by recency
    /// (within the unfinished ones the higher priority comes first), with how many operations match
    /// in total.
    /// </summary>
    public async Task<OperationActivityPage> QueryActivityAsync(
        OperationActivityFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Kinds is { Count: 0 } || filter.Statuses is { Count: 0 })
        {
            return new OperationActivityPage([], 0);
        }

        var (where, parameters) = ActivityWhere(filter.Statuses, filter.Kinds, filter.Search, filter.Priority);
        var limit = Math.Clamp(filter.Limit, 1, 200);
        var offset = Math.Max(filter.Offset, 0);

        return await WithConnectionAsync(
            async connection =>
            {
                int total;
                await using (var count = connection.CreateCommand())
                {
                    count.CommandText = $"""SELECT COUNT(*) FROM "Operations" {where};""";
                    foreach (var parameter in parameters)
                    {
                        Add(count, parameter.Name, parameter.Value);
                    }

                    total = Convert.ToInt32(
                        await count.ExecuteScalarAsync(cancellationToken),
                        CultureInfo.InvariantCulture);
                }

                var items = new List<OperationSnapshot>();
                if (total > 0)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        $"""
                        SELECT "Id", "Kind", "Category", "Lane", "Status", "ProfileId", "Title", "Subject",
                               "ProgressPercent", "Message", "Error", "IsDownload", "BytesTotal",
                               "BytesCompleted", "BytesPerSecond", "EtaUtc", "Attempt", "Retryable",
                               "ExternalProvider", "ExternalId",
                               "CreatedAtUtc", "StartedAtUtc", "FinishedAtUtc", "UpdatedAtUtc", "Details", "ActorProfileId", "Priority"
                        FROM "Operations"
                        {where}
                        ORDER BY
                            CASE "Status" WHEN 2 THEN 0 WHEN 1 THEN 1 WHEN 4 THEN 2 WHEN 6 THEN 3 ELSE 4 END,
                            CASE WHEN "Status" IN (1, 2, 4, 6) THEN "Priority" ELSE 0 END DESC,
                            "UpdatedAtUtc" DESC, "Id" DESC
                        LIMIT {limit} OFFSET {offset};
                        """;
                    foreach (var parameter in parameters)
                    {
                        Add(command, parameter.Name, parameter.Value);
                    }

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        items.Add(ReadOperation(reader));
                    }
                }

                return new OperationActivityPage(items, total);
            },
            cancellationToken);
    }

    /// <summary>
    /// How many operations each kind has in each status among those matching the text; the activity page
    /// turns them into its tab and category counts and works out which page to read.
    /// </summary>
    public async Task<IReadOnlyList<OperationActivityCount>> CountActivityAsync(
        string? search,
        CancellationToken cancellationToken = default)
    {
        var (where, parameters) = ActivityWhere(null, null, search, null);

        return await WithConnectionAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                    SELECT "Kind", "Category", "Status", COUNT(*), "Priority"
                    FROM "Operations"
                    {where}
                    GROUP BY "Kind", "Category", "Status", "Priority";
                    """;
                foreach (var parameter in parameters)
                {
                    Add(command, parameter.Name, parameter.Value);
                }

                var rows = new List<OperationActivityCount>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(new OperationActivityCount(
                        new OperationKindKey(reader.GetString(0), reader.GetString(1)),
                        (OperationStatus)Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                        ReadCount(reader, 3),
                        ReadPriority(reader, 4)));
                }

                return (IReadOnlyList<OperationActivityCount>)rows;
            },
            cancellationToken);
    }

    private static (string Where, List<(string Name, object? Value)> Parameters) ActivityWhere(
        IReadOnlyCollection<OperationStatus>? statuses,
        IReadOnlyCollection<OperationKindKey>? kinds,
        string? search,
        OperationPriority? priority)
    {
        var conditions = new List<string>();
        var parameters = new List<(string Name, object? Value)>();

        if (statuses is { Count: > 0 })
        {
            conditions.Add($"\"Status\" IN ({string.Join(", ", statuses.Select(status => (int)status))})");
        }

        if (priority is { } only)
        {
            conditions.Add("\"Priority\" = @priority");
            parameters.Add(("@priority", (int)only));
        }

        if (kinds is { Count: > 0 })
        {
            var alternatives = new List<string>();
            foreach (var key in kinds)
            {
                var index = parameters.Count;
                alternatives.Add($"(\"Kind\" = @kind{index} AND \"Category\" = @category{index})");
                parameters.Add(($"@kind{index}", key.Kind));
                parameters.Add(($"@category{index}", key.Category));
            }

            conditions.Add("(" + string.Join(" OR ", alternatives) + ")");
        }

        AddSearch(conditions, parameters, search, null);
        return (conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions), parameters);
    }

    public async Task<OperationSummary> GetSummaryAsync(
        CancellationToken cancellationToken = default) =>
        await WithConnectionAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT
                        SUM(CASE WHEN "Status" = 2 THEN 1 ELSE 0 END),
                        SUM(CASE WHEN "Status" = 1 THEN 1 ELSE 0 END),
                        SUM(CASE WHEN "Status" = 4 THEN 1 ELSE 0 END),
                        SUM(CASE WHEN "Status" = 6 THEN 1 ELSE 0 END),
                        SUM(CASE WHEN "IsDownload" = 1 AND "Status" IN (1, 2) THEN 1 ELSE 0 END),
                        SUM(CASE WHEN "Status" = 3 AND "FinishedAtUtc" >= @today THEN 1 ELSE 0 END)
                    FROM "Operations";
                    """;
                Add(command, "@today", Format(DateTime.UtcNow.Date));

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);

                return new OperationSummary(
                    ReadCount(reader, 0),
                    ReadCount(reader, 1),
                    ReadCount(reader, 2),
                    ReadCount(reader, 3),
                    ReadCount(reader, 4),
                    ReadCount(reader, 5));
            },
            cancellationToken);

    public async Task MarkRunningAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await UpdateAsync(
            id,
            """
            "Status" = @status,
            "StartedAtUtc" = COALESCE("StartedAtUtc", @now),
            "FinishedAtUtc" = NULL,
            "Error" = NULL,
            "UpdatedAtUtc" = @now
            """,
            [
                ("@status", (object?)(int)OperationStatus.Running),
                ("@now", Format(now))
            ],
            cancellationToken);

        await AppendLogAsync(
            id,
            OperationLogLevel.Information,
            "Worker",
            "Operation started.",
            cancellationToken);
    }

    public async Task MarkSucceededAsync(
        Guid id,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await UpdateAsync(
            id,
            """
            "Status" = @status,
            "ProgressPercent" = 100,
            "Message" = COALESCE(@message, "Message"),
            "Error" = NULL,
            "FinishedAtUtc" = @now,
            "UpdatedAtUtc" = @now
            """,
            [
                ("@status", (object?)(int)OperationStatus.Succeeded),
                ("@message", Trim(message, MaxMessageLength)),
                ("@now", Format(now))
            ],
            cancellationToken);

        await AppendLogAsync(
            id,
            OperationLogLevel.Information,
            "Worker",
            "Operation completed.",
            cancellationToken);

        await PublishDownloadOrImportEventAsync(id, succeeded: true, error: null, cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid id,
        string error,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await UpdateAsync(
            id,
            """
            "Status" = @status,
            "Error" = @error,
            "FinishedAtUtc" = @now,
            "UpdatedAtUtc" = @now
            """,
            [
                ("@status", (object?)(int)OperationStatus.Failed),
                ("@error", Trim(error, MaxErrorLength)),
                ("@now", Format(now))
            ],
            cancellationToken);

        await AppendLogAsync(
            id,
            OperationLogLevel.Error,
            "Worker",
            Trim(error, MaxMessageLength) ?? "Operation failed.",
            cancellationToken);

        await PublishDownloadOrImportEventAsync(id, succeeded: false, error, cancellationToken);
    }

    /// <summary>
    /// #579: every download and import finishes through <see cref="MarkSucceededAsync"/> or
    /// <see cref="MarkFailedAsync"/>, so this is the one place that turns that outcome into a
    /// #429 domain event — gated on data already present on the snapshot (no schema change):
    /// <see cref="OperationSnapshot.IsDownload"/> for DownloadGrabbed/DownloadFailed, a
    /// <see cref="OperationSnapshot.Kind"/> ending in "-import" for ImportCompleted/ImportFailed.
    /// A download operation is never also an import operation, so at most one event is published.
    /// <see cref="JularrEvent.DedupKey"/> is the operation id: repeated polling of the same stuck
    /// download/import (or a retry that fails again the same way) updates one notification instead
    /// of creating another.
    /// </summary>
    private async Task PublishDownloadOrImportEventAsync(
        Guid id,
        bool succeeded,
        string? error,
        CancellationToken cancellationToken)
    {
        if (events is null)
        {
            return;
        }

        var snapshot = await GetAsync(id, cancellationToken);
        if (snapshot is null)
        {
            return;
        }

        JularrEventCategory category;
        if (snapshot.IsDownload)
        {
            category = succeeded ? JularrEventCategory.DownloadGrabbed : JularrEventCategory.DownloadFailed;
        }
        else if (snapshot.Kind.EndsWith("-import", StringComparison.Ordinal))
        {
            category = succeeded ? JularrEventCategory.ImportCompleted : JularrEventCategory.ImportFailed;
        }
        else
        {
            return;
        }

        var policy = JularrEventCategories.Of(category);
        if (policy.Audience == JularrEventAudience.Profile && string.IsNullOrWhiteSpace(snapshot.ProfileId))
        {
            // System/background operations remain canonical Activity/History records, but a
            // profile-scoped notification must never widen to Admin merely because no profile
            // owns the operation. A real Admin condition needs its own Admin-audience category.
            return;
        }

        var messageParams = new Dictionary<string, string>
        {
            ["title"] = snapshot.Subject ?? snapshot.Title
        };
        if (!succeeded && !string.IsNullOrWhiteSpace(error))
        {
            messageParams["reason"] = error;
        }

        await events.PublishAsync(
            JularrEvent.Create(
                category,
                profileId: snapshot.ProfileId,
                messageParams: messageParams,
                deepLink: $"/Admin/Operation/{id:D}",
                dedupKey: $"operation:{id:D}",
                relatedOperationId: id),
            cancellationToken);
    }

    public async Task MarkCancelledAsync(
        Guid id,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await UpdateAsync(
            id,
            """
            "Status" = @status,
            "Message" = COALESCE(@message, "Message"),
            "FinishedAtUtc" = @now,
            "UpdatedAtUtc" = @now
            """,
            [
                ("@status", (object?)(int)OperationStatus.Cancelled),
                ("@message", Trim(message, MaxMessageLength)),
                ("@now", Format(now))
            ],
            cancellationToken);

        await AppendLogAsync(
            id,
            OperationLogLevel.Warning,
            "Queue",
            message ?? "Operation cancelled.",
            cancellationToken);
    }

    public async Task MarkInterruptedAsync(
        Guid id,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await UpdateAsync(
            id,
            """
            "Status" = @status,
            "Message" = COALESCE(@message, "Message"),
            "FinishedAtUtc" = @now,
            "UpdatedAtUtc" = @now
            """,
            [
                ("@status", (object?)(int)OperationStatus.Interrupted),
                ("@message", Trim(message, MaxMessageLength)),
                ("@now", Format(now))
            ],
            cancellationToken);

        await AppendLogAsync(
            id,
            OperationLogLevel.Warning,
            "Worker",
            message ?? "Operation interrupted.",
            cancellationToken);
    }

    public Task<int> RecoverInterruptedAsync(
        OperationLane lane,
        CancellationToken cancellationToken = default) =>
        RecoverInterruptedAsync(lane, DateTime.UtcNow, cancellationToken);

    // Only work created before the current process owned the lane is abandoned; work queued by
    // this process while recovery was still pending (for example the startup library scan)
    // must stay queued.
    public async Task<int> RecoverInterruptedAsync(
        OperationLane lane,
        DateTime createdBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        return await WithConnectionAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE "Operations"
                    SET "Status" = @interrupted,
                        "Message" = 'Interrupted by server restart.',
                        "FinishedAtUtc" = @now,
                        "UpdatedAtUtc" = @now
                    WHERE "Lane" = @lane
                      AND "Status" IN (@queued, @running)
                      AND ("ExternalProvider" IS NULL OR "ExternalId" IS NULL)
                      AND "CreatedAtUtc" <= @createdBefore;
                    """;
                Add(command, "@interrupted", (int)OperationStatus.Interrupted);
                Add(command, "@now", Format(now));
                Add(command, "@createdBefore", Format(createdBeforeUtc));
                Add(command, "@lane", (int)lane);
                Add(command, "@queued", (int)OperationStatus.Queued);
                Add(command, "@running", (int)OperationStatus.Running);
                return await command.ExecuteNonQueryAsync(cancellationToken);
            },
            cancellationToken);
    }

    public Task SetExternalReferenceAsync(
        Guid id,
        string provider,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        return UpdateAsync(
            id,
            """
            "ExternalProvider" = @provider,
            "ExternalId" = @externalId,
            "UpdatedAtUtc" = @now
            """,
            [
                ("@provider", Trim(provider, MaxExternalProviderLength)),
                ("@externalId", Trim(externalId, MaxExternalIdLength)),
                ("@now", Format(DateTime.UtcNow))
            ],
            cancellationToken);
    }

    /// <summary>
    /// Records the external job and its routing details in one statement, so a crash can never
    /// leave a tracked job without the client it was sent to.
    /// </summary>
    public Task SetExternalReferenceAsync(
        Guid id,
        string provider,
        string externalId,
        string? details,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        return UpdateAsync(
            id,
            """
            "ExternalProvider" = @provider,
            "ExternalId" = @externalId,
            "Details" = @details,
            "UpdatedAtUtc" = @now
            """,
            [
                ("@provider", Trim(provider, MaxExternalProviderLength)),
                ("@externalId", Trim(externalId, MaxExternalIdLength)),
                ("@details", Trim(details, MaxDetailsLength)),
                ("@now", Format(DateTime.UtcNow))
            ],
            cancellationToken);
    }

    /// <summary>
    /// Changes the priority of an operation that is still queued or running; false when it is unknown or
    /// already finished. Queued work is picked up in the new order by the worker of its queue; work that
    /// is already running keeps running.
    /// </summary>
    public async Task<bool> SetPriorityAsync(
        Guid id,
        OperationPriority priority,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }

        var rows = await WithConnectionAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE "Operations"
                    SET "Priority" = @priority
                    WHERE "Id" = @id
                      AND "Status" IN (@queued, @running);
                    """;
                Add(command, "@priority", (int)priority);
                Add(command, "@id", id.ToString("D"));
                Add(command, "@queued", (int)OperationStatus.Queued);
                Add(command, "@running", (int)OperationStatus.Running);
                return await command.ExecuteNonQueryAsync(cancellationToken);
            },
            cancellationToken);

        if (rows > 0)
        {
            await AppendLogAsync(
                id,
                OperationLogLevel.Information,
                "Queue",
                $"Priority set to {OperationPriorities.Name(priority)}.",
                cancellationToken);
        }

        return rows > 0;
    }

    /// <summary>The current priority of each of the given operations; unknown ones are left out.</summary>
    public async Task<IReadOnlyDictionary<Guid, OperationPriority>> GetPrioritiesAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, OperationPriority>();
        }

        return await WithConnectionAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                var names = new List<string>();
                foreach (var id in ids)
                {
                    var name = $"@id{names.Count}";
                    names.Add(name);
                    Add(command, name, id.ToString("D"));
                }

                command.CommandText =
                    $"""SELECT "Id", "Priority" FROM "Operations" WHERE "Id" IN ({string.Join(", ", names)});""";

                var result = new Dictionary<Guid, OperationPriority>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    result[Guid.Parse(reader.GetString(0))] = ReadPriority(reader, 1);
                }

                return (IReadOnlyDictionary<Guid, OperationPriority>)result;
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<OperationSnapshot>> ListActiveExternalAsync(
        string provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        return await WithConnectionAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT "Id", "Kind", "Category", "Lane", "Status", "ProfileId", "Title", "Subject",
                           "ProgressPercent", "Message", "Error", "IsDownload", "BytesTotal",
                           "BytesCompleted", "BytesPerSecond", "EtaUtc", "Attempt", "Retryable",
                           "ExternalProvider", "ExternalId",
                           "CreatedAtUtc", "StartedAtUtc", "FinishedAtUtc", "UpdatedAtUtc", "Details", "ActorProfileId", "Priority"
                    FROM "Operations"
                    WHERE "ExternalProvider" = @provider
                      AND "ExternalId" IS NOT NULL
                      AND "Status" IN (@queued, @running)
                    ORDER BY "UpdatedAtUtc";
                    """;
                Add(command, "@provider", provider.Trim());
                Add(command, "@queued", (int)OperationStatus.Queued);
                Add(command, "@running", (int)OperationStatus.Running);

                var result = new List<OperationSnapshot>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    result.Add(ReadOperation(reader));
                }

                return (IReadOnlyList<OperationSnapshot>)result;
            },
            cancellationToken);
    }

    public async Task<bool> PrepareRetryAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var rows = await WithConnectionAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE "Operations"
                    SET "Status" = @queued,
                        "Attempt" = "Attempt" + 1,
                        "ProgressPercent" = NULL,
                        "Message" = 'Retry queued.',
                        "Error" = NULL,
                        "StartedAtUtc" = NULL,
                        "FinishedAtUtc" = NULL,
                        "UpdatedAtUtc" = @now
                    WHERE "Id" = @id
                      AND "Retryable" = 1
                      AND "Status" IN (@failed, @interrupted);
                    """;
                Add(command, "@queued", (int)OperationStatus.Queued);
                Add(command, "@now", Format(now));
                Add(command, "@id", id.ToString("D"));
                Add(command, "@failed", (int)OperationStatus.Failed);
                Add(command, "@interrupted", (int)OperationStatus.Interrupted);
                return await command.ExecuteNonQueryAsync(cancellationToken);
            },
            cancellationToken);

        if (rows > 0)
        {
            await AppendLogAsync(
                id,
                OperationLogLevel.Information,
                "Queue",
                "Retry queued.",
                cancellationToken);
        }

        return rows > 0;
    }

    // Details is the one structured, kind-specific document of a run (for example
    // the persisted phase and counters of a library scan).
    public Task SetDetailsAsync(
        Guid id,
        string? details,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            id,
            """
            "Details" = @details,
            "UpdatedAtUtc" = @now
            """,
            [
                ("@details", Trim(details, MaxDetailsLength)),
                ("@now", Format(DateTime.UtcNow))
            ],
            cancellationToken);

    // Keeps history bounded for frequently produced kinds: only the newest
    // finished runs of the kind survive; active runs are never touched.
    public async Task<int> PruneFinishedAsync(
        string kind,
        int keep,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentOutOfRangeException.ThrowIfNegative(keep);

        return await WithConnectionAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    DELETE FROM "OperationLogs"
                    WHERE "OperationId" IN (
                        SELECT "Id" FROM "Operations"
                        WHERE "Kind" = @kind AND "Status" IN (3, 4, 5, 6)
                        ORDER BY "UpdatedAtUtc" DESC
                        OFFSET @keep);

                    DELETE FROM "Operations"
                    WHERE "Id" IN (
                        SELECT "Id" FROM "Operations"
                        WHERE "Kind" = @kind AND "Status" IN (3, 4, 5, 6)
                        ORDER BY "UpdatedAtUtc" DESC
                        OFFSET @keep);
                    """;
                Add(command, "@kind", kind.Trim());
                Add(command, "@keep", keep);
                return await command.ExecuteNonQueryAsync(cancellationToken);
            },
            cancellationToken);
    }

    public Task ReportProgressAsync(
        Guid id,
        int? percent,
        string? message = null,
        long? bytesCompleted = null,
        long? bytesTotal = null,
        double? bytesPerSecond = null,
        DateTime? etaUtc = null,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            id,
            """
            "ProgressPercent" = @progress,
            "Message" = COALESCE(@message, "Message"),
            "BytesCompleted" = COALESCE(@bytesCompleted, "BytesCompleted"),
            "BytesTotal" = COALESCE(@bytesTotal, "BytesTotal"),
            "BytesPerSecond" = @bytesPerSecond,
            "EtaUtc" = @etaUtc,
            "UpdatedAtUtc" = @now
            """,
            [
                ("@progress", percent is null ? null : Math.Clamp(percent.Value, 0, 100)),
                ("@message", Trim(message, MaxMessageLength)),
                ("@bytesCompleted", bytesCompleted),
                ("@bytesTotal", bytesTotal),
                ("@bytesPerSecond", bytesPerSecond),
                ("@etaUtc", etaUtc is null ? null : Format(etaUtc.Value)),
                ("@now", Format(DateTime.UtcNow))
            ],
            cancellationToken);

    public async Task AppendLogAsync(
        Guid operationId,
        OperationLogLevel level,
        string module,
        string message,
        CancellationToken cancellationToken = default)
    {
        await WithConnectionAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO "OperationLogs" (
                        "OperationId", "CreatedAtUtc", "Level", "Module", "Message")
                    VALUES (
                        @operationId, @createdAt, @level, @module, @message);
                    """;
                Add(command, "@operationId", operationId.ToString("D"));
                Add(command, "@createdAt", Format(DateTime.UtcNow));
                Add(command, "@level", (int)level);
                Add(command, "@module", Trim(module, MaxModuleLength) ?? "Operation");
                Add(command, "@message", Trim(message, MaxMessageLength) ?? string.Empty);
                await command.ExecuteNonQueryAsync(cancellationToken);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<OperationLogEntry>> ListLogsAsync(
        OperationLogFilter filter,
        CancellationToken cancellationToken = default)
    {
        var conditions = new List<string>();
        var parameters = new List<(string Name, object? Value)>();

        if (filter.Level is { } level)
        {
            conditions.Add("\"Level\" = @level");
            parameters.Add(("@level", (int)level));
        }

        if (filter.OperationId is { } operationId)
        {
            conditions.Add("\"OperationId\" = @operationId");
            parameters.Add(("@operationId", operationId.ToString("D")));
        }

        if (!string.IsNullOrWhiteSpace(filter.Module))
        {
            conditions.Add("\"Module\" = @module");
            parameters.Add(("@module", filter.Module.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            conditions.Add("(\"Message\" ILIKE @search OR \"Module\" ILIKE @search)");
            parameters.Add(("@search", $"%{filter.Search.Trim()}%"));
        }

        var where = conditions.Count == 0
            ? string.Empty
            : "WHERE " + string.Join(" AND ", conditions);
        var limit = Math.Clamp(filter.Limit, 1, 1000);

        return await WithConnectionAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                    SELECT "Id", "OperationId", "CreatedAtUtc", "Level", "Module", "Message"
                    FROM "OperationLogs"
                    {where}
                    ORDER BY "Id" DESC
                    LIMIT {limit};
                    """;

                foreach (var parameter in parameters)
                {
                    Add(command, parameter.Name, parameter.Value);
                }

                var rows = new List<OperationLogEntry>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(new OperationLogEntry(
                        reader.GetInt64(0),
                        Guid.Parse(reader.GetString(1)),
                        ParseDate(reader.GetString(2)),
                        (OperationLogLevel)Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                        reader.GetString(4),
                        reader.GetString(5)));
                }

                return (IReadOnlyList<OperationLogEntry>)rows;
            },
            cancellationToken);
    }

    private Task UpdateAsync(
        Guid id,
        string assignments,
        IReadOnlyList<(string Name, object? Value)> parameters,
        CancellationToken cancellationToken) =>
        WithConnectionAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                    UPDATE "Operations"
                    SET {assignments}
                    WHERE "Id" = @id;
                    """;
                Add(command, "@id", id.ToString("D"));
                foreach (var parameter in parameters)
                {
                    Add(command, parameter.Name, parameter.Value);
                }

                await command.ExecuteNonQueryAsync(cancellationToken);
            },
            cancellationToken);

    private async Task WithConnectionAsync(
        Func<DbConnection, Task> action,
        CancellationToken cancellationToken)
    {
        await WithConnectionAsync(
            async connection =>
            {
                await action(connection);
                return true;
            },
            cancellationToken);
    }

    private async Task<T> WithConnectionAsync<T>(
        Func<DbConnection, Task<T>> action,
        CancellationToken cancellationToken)
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

    private static OperationSnapshot ReadOperation(DbDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            (OperationLane)Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
            (OperationStatus)Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture),
            ReadNullableString(reader, 5),
            reader.GetString(6),
            ReadNullableString(reader, 7),
            ReadNullableInt(reader, 8),
            ReadNullableString(reader, 9),
            ReadNullableString(reader, 10),
            Convert.ToInt32(reader.GetValue(11), CultureInfo.InvariantCulture) != 0,
            ReadNullableLong(reader, 12),
            ReadNullableLong(reader, 13),
            ReadNullableDouble(reader, 14),
            ReadNullableDate(reader, 15),
            Convert.ToInt32(reader.GetValue(16), CultureInfo.InvariantCulture),
            Convert.ToInt32(reader.GetValue(17), CultureInfo.InvariantCulture) != 0,
            ReadNullableString(reader, 18),
            ReadNullableString(reader, 19),
            ParseDate(reader.GetString(20)),
            ReadNullableDate(reader, 21),
            ReadNullableDate(reader, 22),
            ParseDate(reader.GetString(23)),
            ReadNullableString(reader, 24),
            ReadNullableString(reader, 25),
            ReadPriority(reader, 26));

    private static OperationPriority ReadPriority(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? OperationPriority.Normal
            : (OperationPriority)Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static int ReadCount(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? 0
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static int? ReadNullableInt(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static long? ReadNullableLong(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static double? ReadNullableDouble(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static string? ReadNullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTime? ReadNullableDate(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));

    private static DateTime ParseDate(string value) =>
        DateTime.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static string Format(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = value.Trim();
        return cleaned.Length <= maxLength
            ? cleaned
            : cleaned[..maxLength];
    }

    private static void Add(
        DbCommand command,
        string name,
        object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
