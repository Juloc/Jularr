using System.Data;
using System.Data.Common;
using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Watchlist;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Franchises;

/// <summary>
/// Shared franchise data (tables Franchises and FranchiseMembers). Everything stored here comes
/// from the provider through <see cref="FranchiseService"/>; browser input only ever names the
/// seed's identity.
/// </summary>
public sealed class FranchiseStore(AppDbContext db)
{
    /// <summary>
    /// The franchise grown from <paramref name="seed"/>, created without a title or members when
    /// it does not exist yet. The next refresh reads both from the provider.
    /// </summary>
    public async Task<Guid> GetOrCreateBySeedAsync(WatchlistIdentity seed, CancellationToken cancellationToken)
    {
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "Franchises"
                    ("Id", "Title", "SeedMediaType", "SeedProvider", "SeedExternalId", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES
                    (@id, '', @mediaType, @provider, @externalId, @created, @created)
                ON CONFLICT ("SeedMediaType", "SeedProvider", "SeedExternalId") DO NOTHING;
                """;
            Add(command, "@id", Guid.NewGuid().ToString("D"));
            Add(command, "@mediaType", WatchlistMediaTypeNames.ToStorage(seed.MediaType));
            Add(command, "@provider", seed.ProviderKey);
            Add(command, "@externalId", seed.ExternalKey);
            Add(command, "@created", Timestamp(DateTime.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

        return await FindBySeedAsync(seed, cancellationToken)
            ?? throw new InvalidOperationException("The franchise could not be created.");
    }

    public Task FollowAsync(string profileId, Guid franchiseId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "ProfileFranchiseFollows" ("ProfileId", "FranchiseId", "FollowedAtUtc")
                VALUES (@profile, @franchise, @followed)
                ON CONFLICT ("ProfileId", "FranchiseId") DO NOTHING;
                """;
            Add(command, "@profile", profileId);
            Add(command, "@franchise", franchiseId.ToString("D"));
            Add(command, "@followed", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public Task UnfollowAsync(string profileId, Guid franchiseId, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM "ProfileFranchiseFollows"
                WHERE "ProfileId" = @profile AND "FranchiseId" = @franchise;
                """;
            Add(command, "@profile", profileId);
            Add(command, "@franchise", franchiseId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// Stores provider data of one member. The relation a member was first found through is kept
    /// (the seed has none), so a later refresh from another member does not relabel it.
    /// </summary>
    public Task UpsertMemberAsync(
        Guid franchiseId,
        WatchlistDraft media,
        string? relationType,
        bool isSeed,
        CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "FranchiseMembers"
                    ("FranchiseId", "MediaType", "Provider", "ExternalId", "Title", "NativeTitle",
                     "CoverImageUrl", "Format", "Status", "Year", "RelationType", "IsSeed", "UpdatedAtUtc")
                VALUES
                    (@franchise, @mediaType, @provider, @externalId, @title, @nativeTitle,
                     @cover, @format, @status, @year, @relationType, @isSeed, @updated)
                ON CONFLICT ("FranchiseId", "MediaType", "Provider", "ExternalId") DO UPDATE SET
                    "Title" = excluded."Title",
                    "NativeTitle" = COALESCE(excluded."NativeTitle", "FranchiseMembers"."NativeTitle"),
                    "CoverImageUrl" = COALESCE(excluded."CoverImageUrl", "FranchiseMembers"."CoverImageUrl"),
                    "Format" = COALESCE(excluded."Format", "FranchiseMembers"."Format"),
                    "Status" = COALESCE(excluded."Status", "FranchiseMembers"."Status"),
                    "Year" = COALESCE(excluded."Year", "FranchiseMembers"."Year"),
                    "RelationType" = CASE
                        WHEN "FranchiseMembers"."IsSeed" = 1 OR excluded."IsSeed" = 1 THEN NULL
                        ELSE COALESCE("FranchiseMembers"."RelationType", excluded."RelationType")
                    END,
                    "IsSeed" = CASE WHEN excluded."IsSeed" = 1 THEN 1 ELSE "FranchiseMembers"."IsSeed" END,
                    "UpdatedAtUtc" = excluded."UpdatedAtUtc";
                """;
            Add(command, "@franchise", franchiseId.ToString("D"));
            Add(command, "@mediaType", WatchlistMediaTypeNames.ToStorage(media.Identity.MediaType));
            Add(command, "@provider", media.Identity.ProviderKey);
            Add(command, "@externalId", media.Identity.ExternalKey);
            Add(command, "@title", media.Title.Trim());
            Add(command, "@nativeTitle", Clean(media.NativeTitle));
            Add(command, "@cover", Clean(media.CoverImageUrl));
            Add(command, "@format", Clean(media.Format));
            Add(command, "@status", Clean(media.Status));
            Add(command, "@year", media.Year);
            Add(command, "@relationType", isSeed ? null : Clean(relationType));
            Add(command, "@isSeed", isSeed ? 1 : 0);
            Add(command, "@updated", Timestamp(DateTime.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public async Task<IReadOnlyList<FranchiseMember>> GetMembersAsync(Guid franchiseId, CancellationToken cancellationToken) =>
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT "MediaType", "Provider", "ExternalId", "Title", "NativeTitle",
                       "CoverImageUrl", "Format", "Status", "Year",
                       "RelationType", "IsSeed", "RelationsCheckedAtUtc"
                FROM "FranchiseMembers"
                WHERE "FranchiseId" = @franchise
                ORDER BY "IsSeed" DESC, "Title";
                """;
            Add(command, "@franchise", franchiseId.ToString("D"));
            var result = new List<FranchiseMember>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var type = WatchlistMediaTypeNames.Parse(reader.GetString(0));
                if (type is null) continue;

                var draft = new WatchlistDraft(
                    new WatchlistIdentity(type.Value, reader.GetString(1), reader.GetString(2)),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8));
                result.Add(new FranchiseMember(
                    franchiseId,
                    draft,
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetInt32(10) != 0,
                    ParseTimestamp(reader, 11)));
            }

            return result.ToArray();
        }, cancellationToken);

    public Task MarkMemberCheckedAsync(
        Guid franchiseId,
        WatchlistIdentity member,
        DateTime checkedAtUtc,
        CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE "FranchiseMembers"
                SET "RelationsCheckedAtUtc" = @checked
                WHERE "FranchiseId" = @franchise
                  AND "MediaType" = @mediaType
                  AND "Provider" = @provider
                  AND "ExternalId" = @externalId;
                """;
            Add(command, "@checked", Timestamp(checkedAtUtc));
            Add(command, "@franchise", franchiseId.ToString("D"));
            Add(command, "@mediaType", WatchlistMediaTypeNames.ToStorage(member.MediaType));
            Add(command, "@provider", member.ProviderKey);
            Add(command, "@externalId", member.ExternalKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public Task SetTitleAsync(Guid franchiseId, string title, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE "Franchises"
                SET "Title" = @title, "UpdatedAtUtc" = @updated
                WHERE "Id" = @id AND "Title" <> @title;
                """;
            Add(command, "@title", title.Trim());
            Add(command, "@updated", Timestamp(DateTime.UtcNow));
            Add(command, "@id", franchiseId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>When a full refresh of the franchise was last asked for, if ever.</summary>
    public async Task<DateTime?> GetRefreshRequestedAsync(Guid franchiseId, CancellationToken cancellationToken) =>
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """SELECT "RefreshRequestedAtUtc" FROM "Franchises" WHERE "Id" = @id;""";
            Add(command, "@id", franchiseId.ToString("D"));
            return await command.ExecuteScalarAsync(cancellationToken) is string text ? ParseTimestamp(text) : null;
        }, cancellationToken);

    /// <summary>
    /// Asks for a full refresh unless one was asked for, or finished, after <paramref name="cooldownStartUtc"/>.
    /// Returns whether the request was recorded.
    /// </summary>
    public async Task<bool> TryRequestRefreshAsync(
        Guid franchiseId,
        DateTime nowUtc,
        DateTime cooldownStartUtc,
        CancellationToken cancellationToken) =>
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE "Franchises"
                SET "RefreshRequestedAtUtc" = @now
                WHERE "Id" = @id
                  AND COALESCE("RefreshRequestedAtUtc", '') < @cooldown
                  AND COALESCE("LastRefreshedAtUtc", '') < @cooldown;
                """;
            Add(command, "@now", Timestamp(nowUtc));
            Add(command, "@cooldown", Timestamp(cooldownStartUtc));
            Add(command, "@id", franchiseId.ToString("D"));
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }, cancellationToken);

    /// <summary>
    /// Followed franchises with work to do: no seed member yet, or a provider member whose
    /// relations were never read, were read before the last refresh request, or before
    /// <paramref name="recheckBeforeUtc"/>. Least recently refreshed first.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ListRefreshDueAsync(
        DateTime recheckBeforeUtc,
        CancellationToken cancellationToken) =>
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT f."Id"
                FROM "Franchises" f
                WHERE EXISTS (SELECT 1 FROM "ProfileFranchiseFollows" pf WHERE pf."FranchiseId" = f."Id")
                  AND (
                      NOT EXISTS (
                          SELECT 1 FROM "FranchiseMembers" s
                          WHERE s."FranchiseId" = f."Id" AND s."IsSeed" = 1)
                      OR EXISTS (
                          SELECT 1 FROM "FranchiseMembers" m
                          WHERE m."FranchiseId" = f."Id"
                            AND m."Provider" = 'anilist'
                            AND m."MediaType" IN ('anime', 'manga', 'lightNovel')
                            AND (m."RelationsCheckedAtUtc" IS NULL
                                 OR m."RelationsCheckedAtUtc" < @recheck
                                 OR m."RelationsCheckedAtUtc" < COALESCE(f."RefreshRequestedAtUtc", ''))))
                ORDER BY COALESCE(f."LastRefreshedAtUtc", '');
                """;
            Add(command, "@recheck", Timestamp(recheckBeforeUtc));
            var result = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (Guid.TryParse(reader.GetString(0), out var id)) result.Add(id);
            }

            return result.ToArray();
        }, cancellationToken);

    public async Task<FranchiseSummary?> GetAsync(
        Guid franchiseId,
        CancellationToken cancellationToken) =>
        await WithConnectionAsync<FranchiseSummary?>(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT f."Id", f."Title", f."SeedMediaType", f."SeedProvider", f."SeedExternalId",
                       f."LastRefreshedAtUtc",
                       (SELECT COUNT(*) FROM "FranchiseMembers" m WHERE m."FranchiseId" = f."Id")
                FROM "Franchises" f
                WHERE f."Id" = @id
                LIMIT 1;
                """;
            Add(command, "@id", franchiseId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var mediaType = WatchlistMediaTypeNames.Parse(reader.GetString(2));
            if (mediaType is null)
            {
                return null;
            }

            DateTime? refreshed = null;
            if (!reader.IsDBNull(5) &&
                DateTime.TryParse(
                    reader.GetString(5),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                refreshed = parsed;
            }

            return new FranchiseSummary(
                franchiseId,
                reader.GetString(1),
                new WatchlistIdentity(mediaType.Value, reader.GetString(3), reader.GetString(4)),
                refreshed,
                Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture));
        }, cancellationToken);

    public async Task<bool> IsFollowedAsync(
        string profileId,
        Guid franchiseId,
        CancellationToken cancellationToken) =>
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT 1
                FROM "ProfileFranchiseFollows"
                WHERE "ProfileId" = @profile AND "FranchiseId" = @franchise
                LIMIT 1;
                """;
            Add(command, "@profile", profileId);
            Add(command, "@franchise", franchiseId.ToString("D"));
            return await command.ExecuteScalarAsync(cancellationToken) is not null;
        }, cancellationToken);

    public async Task<IReadOnlyList<FranchiseSummary>> FindForMemberAsync(
        WatchlistIdentity identity,
        CancellationToken cancellationToken) =>
        await WithConnectionAsync<IReadOnlyList<FranchiseSummary>>(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT f."Id", f."Title", f."SeedMediaType", f."SeedProvider", f."SeedExternalId",
                       f."LastRefreshedAtUtc",
                       (SELECT COUNT(*) FROM "FranchiseMembers" countMember WHERE countMember."FranchiseId" = f."Id")
                FROM "Franchises" f
                INNER JOIN "FranchiseMembers" m ON m."FranchiseId" = f."Id"
                WHERE m."MediaType" = @mediaType
                  AND m."Provider" = @provider
                  AND m."ExternalId" = @externalId
                ORDER BY f."Title";
                """;
            Add(command, "@mediaType", WatchlistMediaTypeNames.ToStorage(identity.MediaType));
            Add(command, "@provider", identity.ProviderKey);
            Add(command, "@externalId", identity.ExternalKey);

            var result = new List<FranchiseSummary>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!Guid.TryParse(reader.GetString(0), out var id) ||
                    WatchlistMediaTypeNames.Parse(reader.GetString(2)) is not { } seedType)
                {
                    continue;
                }

                DateTime? refreshed = null;
                if (!reader.IsDBNull(5) &&
                    DateTime.TryParse(
                        reader.GetString(5),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var parsed))
                {
                    refreshed = parsed;
                }

                result.Add(new FranchiseSummary(
                    id,
                    reader.GetString(1),
                    new WatchlistIdentity(seedType, reader.GetString(3), reader.GetString(4)),
                    refreshed,
                    Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture)));
            }

            return result.ToArray();
        }, cancellationToken);

    public async Task<PageResult<FranchiseSummary>> ListFollowedAsync(
        string profileId,
        PageRequest paging,
        CancellationToken cancellationToken) =>
        await WithConnectionAsync(async connection =>
        {
            var result = new List<FranchiseSummary>(paging.PageSize);
            long? totalCount = null;

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT
                        "Franchises"."Id",
                        "Franchises"."Title",
                        "Franchises"."SeedMediaType",
                        "Franchises"."SeedProvider",
                        "Franchises"."SeedExternalId",
                        "Franchises"."LastRefreshedAtUtc",
                        (
                            SELECT
                                COUNT(*)
                            FROM
                                "FranchiseMembers"
                            WHERE
                                "FranchiseMembers"."FranchiseId" = "Franchises"."Id"
                        ) AS "MemberCount",
                        COUNT(*) OVER () AS "TotalCount"
                    FROM
                        "Franchises"
                    INNER JOIN
                        "ProfileFranchiseFollows"
                            ON "ProfileFranchiseFollows"."FranchiseId" = "Franchises"."Id"
                    WHERE
                        "ProfileFranchiseFollows"."ProfileId" = @ProfileId
                    ORDER BY
                        "Franchises"."Title" ASC,
                        "Franchises"."Id" ASC
                    LIMIT
                        @PageSize
                    OFFSET
                        @Offset
                    """;
                foreach (var parameter in SqlParams.Create()
                    .Add("ProfileId", profileId).ToArray().Concat(paging.ToSqlParameters()))
                {
                    command.Parameters.Add(parameter);
                }

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    totalCount ??= reader.GetInt64(7);
                    var mediaType = WatchlistMediaTypeNames.Parse(reader.GetString(2));
                    if (mediaType is null || !Guid.TryParse(reader.GetString(0), out var id))
                    {
                        continue;
                    }

                    DateTime? refreshed = null;
                    if (!reader.IsDBNull(5) &&
                        DateTime.TryParse(
                            reader.GetString(5),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var parsed))
                    {
                        refreshed = parsed;
                    }

                    result.Add(new FranchiseSummary(
                        id,
                        reader.GetString(1),
                        new WatchlistIdentity(mediaType.Value, reader.GetString(3), reader.GetString(4)),
                        refreshed,
                        Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture)));
                }
            }

            if (totalCount is null)
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT
                        COUNT(*)
                    FROM
                        "ProfileFranchiseFollows"
                    WHERE
                        "ProfileId" = @ProfileId
                    """;
                foreach (var parameter in SqlParams.Create().Add("ProfileId", profileId).ToArray())
                {
                    command.Parameters.Add(parameter);
                }

                totalCount = Convert.ToInt64(
                    await command.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture);
            }

            return PageResult<FranchiseSummary>.From(
                result,
                paging,
                totalCount,
                paging.Offset + result.Count < totalCount.Value);
        }, cancellationToken);

    /// <summary>Records that every member's relations are current.</summary>
    public Task MarkRefreshedAsync(Guid franchiseId, DateTime refreshedAtUtc, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE "Franchises"
                SET "LastRefreshedAtUtc" = @refreshed, "UpdatedAtUtc" = @refreshed
                WHERE "Id" = @id;
                """;
            Add(command, "@id", franchiseId.ToString("D"));
            Add(command, "@refreshed", Timestamp(refreshedAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    private async Task<Guid?> FindBySeedAsync(WatchlistIdentity seed, CancellationToken cancellationToken) =>
        await WithConnectionAsync<Guid?>(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT "Id" FROM "Franchises"
                WHERE "SeedMediaType" = @mediaType
                  AND "SeedProvider" = @provider
                  AND "SeedExternalId" = @externalId
                LIMIT 1;
                """;
            Add(command, "@mediaType", WatchlistMediaTypeNames.ToStorage(seed.MediaType));
            Add(command, "@provider", seed.ProviderKey);
            Add(command, "@externalId", seed.ExternalKey);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is string text && Guid.TryParse(text, out var id) ? id : null;
        }, cancellationToken);

    private async Task<T> WithConnectionAsync<T>(
        Func<DbConnection, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(cancellationToken);
        try
        {
            return await action(connection);
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    private Task WithConnectionAsync(Func<DbConnection, Task> action, CancellationToken cancellationToken) =>
        WithConnectionAsync<bool>(async connection =>
        {
            await action(connection);
            return true;
        }, cancellationToken);

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Stored timestamps are round-trip UTC text, so they also compare correctly as strings.</summary>
    private static string Timestamp(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static DateTime? ParseTimestamp(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static DateTime? ParseTimestamp(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));
}
