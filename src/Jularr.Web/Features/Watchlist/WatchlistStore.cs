using System.Data;
using System.Data.Common;
using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Jularr.Web.Features.Watchlist;

/// <summary>
/// Profile-scoped local follow state. Provider accounts are never the source of truth: provider
/// identity only identifies a work so metadata/release adapters can enrich it.
/// </summary>
public sealed class WatchlistStore(AppDbContext db)
{
    public async Task FollowAsync(string profileId, WatchlistDraft draft, CancellationToken cancellationToken)
    {
        Validate(profileId, draft);
        await UpsertPreferenceAsync(profileId, draft, WatchPreferenceState.Follow, cancellationToken);
    }

    public async Task UnfollowAsync(string profileId, WatchlistIdentity identity, CancellationToken cancellationToken)
    {
        ValidateProfile(profileId);
        if (await IsIncludedByFollowedFranchiseAsync(profileId, identity, cancellationToken))
        {
            var existing = (await GetEffectivePageAsync(
                CurrentAccountContext.ForProfile(profileId),
                new PageRequest(),
                [identity.MediaType],
                cancellationToken,
                identity)).Items.FirstOrDefault();
            var draft = existing is null
                ? new WatchlistDraft(identity, identity.ExternalKey)
                : new WatchlistDraft(
                    identity,
                    existing.Title,
                    existing.NativeTitle,
                    existing.CoverImageUrl,
                    existing.Format,
                    existing.Status,
                    existing.Year);
            await UpsertPreferenceAsync(profileId, draft, WatchPreferenceState.Ignore, cancellationToken);
            return;
        }

        await DeletePreferenceAsync(profileId, identity, cancellationToken);
    }

    /// <summary>
    /// Undoes hiding a franchise work: the work is followed through its franchise again. Direct
    /// follows are not touched.
    /// </summary>
    public async Task RestoreAsync(string profileId, WatchlistIdentity identity, CancellationToken cancellationToken)
    {
        ValidateProfile(profileId);
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM "ProfileWatchlistPreferences"
                WHERE "ProfileId" = @profile
                  AND "MediaType" = @mediaType
                  AND "Provider" = @provider
                  AND "ExternalId" = @externalId
                  AND "FollowState" = 'ignore';
                """;
            Add(command, "@profile", profileId);
            Add(command, "@mediaType", WatchlistMediaTypeNames.ToStorage(identity.MediaType));
            Add(command, "@provider", identity.ProviderKey);
            Add(command, "@externalId", identity.ExternalKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Keys of the franchise works this profile has hidden.</summary>
    public async Task<IReadOnlySet<string>> GetHiddenKeysAsync(string profileId, CancellationToken cancellationToken)
    {
        ValidateProfile(profileId);
        return await WithConnectionAsync<IReadOnlySet<string>>(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT "MediaType", "Provider", "ExternalId"
                FROM "ProfileWatchlistPreferences"
                WHERE "ProfileId" = @profile AND "FollowState" = 'ignore';
                """;
            Add(command, "@profile", profileId);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (WatchlistMediaTypeNames.Parse(reader.GetString(0)) is { } type)
                {
                    keys.Add(new WatchlistIdentity(type, reader.GetString(1), reader.GetString(2)).Key);
                }
            }

            return keys;
        }, cancellationToken);
    }

    private Task DeletePreferenceAsync(string profileId, WatchlistIdentity identity, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM "ProfileWatchlistPreferences"
                WHERE "ProfileId" = @profile
                  AND "MediaType" = @mediaType
                  AND "Provider" = @provider
                  AND "ExternalId" = @externalId;
                """;
            Add(command, "@profile", profileId);
            Add(command, "@mediaType", WatchlistMediaTypeNames.ToStorage(identity.MediaType));
            Add(command, "@provider", identity.ProviderKey);
            Add(command, "@externalId", identity.ExternalKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);


    /// <summary>
    /// Resolve only the currently displayed Discover identities; never materialize a full profile
    /// watchlist for a bounded card shelf. This is a profile-scoped, fixed-SQL point lookup.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, Guid?>> GetFollowedFranchiseIdsForKeysAsync(
        CurrentAccountContext account,
        IReadOnlyCollection<WatchlistIdentity> identities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(identities);

        var profileId = account.ProfileId;
        ValidateProfile(profileId);
        var keys = identities
            .Select(identity => identity.Key)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var result = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        if (keys.Length == 0)
        {
            return result;
        }

        await WithConnectionAsync(async connection =>
        {
            foreach (var batch in keys.Chunk(PageRequest.MaximumPageSize))
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    WITH "Candidates" AS (
                        SELECT
                            "FranchiseMembers"."MediaType",
                            "FranchiseMembers"."Provider",
                            "FranchiseMembers"."ExternalId",
                            "Franchises"."Id" AS "FranchiseId",
                            "Franchises"."Title" AS "FranchiseTitle"
                        FROM
                            "FranchiseMembers"
                        INNER JOIN
                            "ProfileFranchiseFollows"
                                ON "ProfileFranchiseFollows"."FranchiseId" = "FranchiseMembers"."FranchiseId"
                        INNER JOIN
                            "Franchises"
                                ON "Franchises"."Id" = "FranchiseMembers"."FranchiseId"
                        WHERE
                            "ProfileFranchiseFollows"."ProfileId" = @ProfileId
                            AND (
                                "FranchiseMembers"."MediaType" || ':' ||
                                "FranchiseMembers"."Provider" || ':' ||
                                "FranchiseMembers"."ExternalId"
                            ) = ANY(@Keys)
                        UNION ALL
                        SELECT
                            "ProfileWatchlistPreferences"."MediaType",
                            "ProfileWatchlistPreferences"."Provider",
                            "ProfileWatchlistPreferences"."ExternalId",
                            NULL AS "FranchiseId",
                            NULL AS "FranchiseTitle"
                        FROM
                            "ProfileWatchlistPreferences"
                        WHERE
                            "ProfileWatchlistPreferences"."ProfileId" = @ProfileId
                            AND "ProfileWatchlistPreferences"."FollowState" = 'follow'
                            AND (
                                "ProfileWatchlistPreferences"."MediaType" || ':' ||
                                "ProfileWatchlistPreferences"."Provider" || ':' ||
                                "ProfileWatchlistPreferences"."ExternalId"
                            ) = ANY(@Keys)
                    )
                    SELECT DISTINCT ON (
                        "Candidates"."MediaType",
                        "Candidates"."Provider",
                        "Candidates"."ExternalId"
                    )
                        "Candidates"."MediaType",
                        "Candidates"."Provider",
                        "Candidates"."ExternalId",
                        "Candidates"."FranchiseId"
                    FROM
                        "Candidates"
                    WHERE
                        NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "ProfileWatchlistPreferences" AS "Ignored"
                            WHERE
                                "Ignored"."ProfileId" = @ProfileId
                                AND "Ignored"."FollowState" = 'ignore'
                                AND "Ignored"."MediaType" = "Candidates"."MediaType"
                                AND "Ignored"."Provider" = "Candidates"."Provider"
                                AND "Ignored"."ExternalId" = "Candidates"."ExternalId"
                        )
                    ORDER BY
                        "Candidates"."MediaType" ASC,
                        "Candidates"."Provider" ASC,
                        "Candidates"."ExternalId" ASC,
                        "Candidates"."FranchiseTitle" ASC NULLS LAST,
                        "Candidates"."FranchiseId" ASC NULLS LAST
                    LIMIT
                        @MaxResults
                    """;
                foreach (var parameter in SqlParams.Create()
                    .Add("ProfileId", profileId)
                    .Add("Keys", batch, NpgsqlDbType.Array | NpgsqlDbType.Text)
                    .Add("MaxResults", batch.Length)
                    .ToArray())
                {
                    command.Parameters.Add(parameter);
                }

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var mediaType = WatchlistMediaTypeNames.Parse(reader.GetString(0));
                    if (mediaType is null)
                    {
                        continue;
                    }

                    var key = new WatchlistIdentity(
                        mediaType.Value,
                        reader.GetString(1),
                        reader.GetString(2)).Key;
                    Guid? franchiseId = null;
                    if (!reader.IsDBNull(3) &&
                        Guid.TryParse(reader.GetString(3), out var parsed))
                    {
                        franchiseId = parsed;
                    }

                    result[key] = franchiseId;
                }
            }
        }, cancellationToken);

        return result;
    }

    private const string ReadEffectivePageSql = """
        WITH "InheritedRanks" AS (
            SELECT
                "FranchiseMembers"."MediaType",
                "FranchiseMembers"."Provider",
                "FranchiseMembers"."ExternalId",
                "FranchiseMembers"."Title",
                "FranchiseMembers"."NativeTitle",
                "FranchiseMembers"."CoverImageUrl",
                "FranchiseMembers"."Format",
                "FranchiseMembers"."Status",
                "FranchiseMembers"."Year",
                "Franchises"."Id" AS "FranchiseId",
                "Franchises"."Title" AS "FranchiseTitle",
                ROW_NUMBER() OVER (
                    PARTITION BY
                        "FranchiseMembers"."MediaType",
                        "FranchiseMembers"."Provider",
                        "FranchiseMembers"."ExternalId"
                    ORDER BY
                        "Franchises"."Title" ASC,
                        "Franchises"."Id" ASC
                ) AS "RowNumber"
            FROM
                "FranchiseMembers"
            INNER JOIN
                "ProfileFranchiseFollows"
                    ON "ProfileFranchiseFollows"."FranchiseId" = "FranchiseMembers"."FranchiseId"
            INNER JOIN
                "Franchises"
                    ON "Franchises"."Id" = "FranchiseMembers"."FranchiseId"
            WHERE
                "ProfileFranchiseFollows"."ProfileId" = @ProfileId
        ),
        "Inherited" AS (
            SELECT
                "InheritedRanks"."MediaType",
                "InheritedRanks"."Provider",
                "InheritedRanks"."ExternalId",
                "InheritedRanks"."Title",
                "InheritedRanks"."NativeTitle",
                "InheritedRanks"."CoverImageUrl",
                "InheritedRanks"."Format",
                "InheritedRanks"."Status",
                "InheritedRanks"."Year",
                "InheritedRanks"."FranchiseId",
                "InheritedRanks"."FranchiseTitle"
            FROM
                "InheritedRanks"
            WHERE
                "InheritedRanks"."RowNumber" = 1
        ),
        "Direct" AS (
            SELECT
                "ProfileWatchlistPreferences"."MediaType",
                "ProfileWatchlistPreferences"."Provider",
                "ProfileWatchlistPreferences"."ExternalId",
                "ProfileWatchlistPreferences"."Title",
                "ProfileWatchlistPreferences"."NativeTitle",
                "ProfileWatchlistPreferences"."CoverImageUrl",
                "ProfileWatchlistPreferences"."Format",
                "ProfileWatchlistPreferences"."Status",
                "ProfileWatchlistPreferences"."Year",
                "ProfileWatchlistPreferences"."UpdatedAtUtc"
            FROM
                "ProfileWatchlistPreferences"
            WHERE
                "ProfileWatchlistPreferences"."ProfileId" = @ProfileId
                AND "ProfileWatchlistPreferences"."FollowState" = 'follow'
        ),
        "Effective" AS (
            SELECT
                COALESCE("Direct"."MediaType", "Inherited"."MediaType") AS "MediaType",
                COALESCE("Direct"."Provider", "Inherited"."Provider") AS "Provider",
                COALESCE("Direct"."ExternalId", "Inherited"."ExternalId") AS "ExternalId",
                COALESCE("Direct"."Title", "Inherited"."Title") AS "Title",
                CASE
                    WHEN "Direct"."MediaType" IS NOT NULL THEN "Direct"."NativeTitle"
                    ELSE "Inherited"."NativeTitle"
                END AS "NativeTitle",
                CASE
                    WHEN "Direct"."MediaType" IS NOT NULL THEN "Direct"."CoverImageUrl"
                    ELSE "Inherited"."CoverImageUrl"
                END AS "CoverImageUrl",
                CASE
                    WHEN "Direct"."MediaType" IS NOT NULL THEN "Direct"."Format"
                    ELSE "Inherited"."Format"
                END AS "Format",
                CASE
                    WHEN "Direct"."MediaType" IS NOT NULL THEN "Direct"."Status"
                    ELSE "Inherited"."Status"
                END AS "Status",
                CASE
                    WHEN "Direct"."MediaType" IS NOT NULL THEN "Direct"."Year"
                    ELSE "Inherited"."Year"
                END AS "Year",
                "Inherited"."FranchiseId",
                "Inherited"."FranchiseTitle",
                "Direct"."UpdatedAtUtc",
                ("Direct"."MediaType" IS NOT NULL) AS "IsExplicit"
            FROM
                "Inherited"
            FULL OUTER JOIN
                "Direct"
                    ON "Direct"."MediaType" = "Inherited"."MediaType"
                    AND "Direct"."Provider" = "Inherited"."Provider"
                    AND "Direct"."ExternalId" = "Inherited"."ExternalId"
        )
        SELECT
            "Effective"."MediaType",
            "Effective"."Provider",
            "Effective"."ExternalId",
            "Effective"."Title",
            "Effective"."NativeTitle",
            "Effective"."CoverImageUrl",
            "Effective"."Format",
            "Effective"."Status",
            "Effective"."Year",
            "Effective"."FranchiseId",
            "Effective"."FranchiseTitle",
            "Effective"."UpdatedAtUtc",
            "Effective"."IsExplicit",
            COUNT(*) OVER () AS "TotalCount"
        FROM
            "Effective"
        WHERE
            "Effective"."MediaType" = ANY(@VisibleMediaTypes)
            AND (
                @TargetMediaType IS NULL
                OR (
                    "Effective"."MediaType" = @TargetMediaType
                    AND "Effective"."Provider" = @TargetProvider
                    AND "Effective"."ExternalId" = @TargetExternalId
                )
            )
            AND NOT EXISTS (
                SELECT
                    1
                FROM
                    "ProfileWatchlistPreferences" AS "Ignored"
                WHERE
                    "Ignored"."ProfileId" = @ProfileId
                    AND "Ignored"."FollowState" = 'ignore'
                    AND "Ignored"."MediaType" = "Effective"."MediaType"
                    AND "Ignored"."Provider" = "Effective"."Provider"
                    AND "Ignored"."ExternalId" = "Effective"."ExternalId"
            )
        ORDER BY
            LOWER("Effective"."Title") ASC,
            "Effective"."MediaType" ASC,
            "Effective"."Provider" ASC,
            "Effective"."ExternalId" ASC
        LIMIT
            @PageSize
        OFFSET
            @Offset
        """;

    private const string CountEffectiveSql = """
        WITH "Candidates" AS (
            SELECT
                "FranchiseMembers"."MediaType",
                "FranchiseMembers"."Provider",
                "FranchiseMembers"."ExternalId"
            FROM
                "FranchiseMembers"
            INNER JOIN
                "ProfileFranchiseFollows"
                    ON "ProfileFranchiseFollows"."FranchiseId" = "FranchiseMembers"."FranchiseId"
            WHERE
                "ProfileFranchiseFollows"."ProfileId" = @ProfileId
            UNION
            SELECT
                "ProfileWatchlistPreferences"."MediaType",
                "ProfileWatchlistPreferences"."Provider",
                "ProfileWatchlistPreferences"."ExternalId"
            FROM
                "ProfileWatchlistPreferences"
            WHERE
                "ProfileWatchlistPreferences"."ProfileId" = @ProfileId
                AND "ProfileWatchlistPreferences"."FollowState" = 'follow'
        )
        SELECT
            COUNT(*)
        FROM
            "Candidates"
        WHERE
            "Candidates"."MediaType" = ANY(@VisibleMediaTypes)
            AND (
                @TargetMediaType IS NULL
                OR (
                    "Candidates"."MediaType" = @TargetMediaType
                    AND "Candidates"."Provider" = @TargetProvider
                    AND "Candidates"."ExternalId" = @TargetExternalId
                )
            )
            AND NOT EXISTS (
                SELECT
                    1
                FROM
                    "ProfileWatchlistPreferences" AS "Ignored"
                WHERE
                    "Ignored"."ProfileId" = @ProfileId
                    AND "Ignored"."FollowState" = 'ignore'
                    AND "Ignored"."MediaType" = "Candidates"."MediaType"
                    AND "Ignored"."Provider" = "Candidates"."Provider"
                    AND "Ignored"."ExternalId" = "Candidates"."ExternalId"
            )
        """;

    public async Task<PageResult<WatchlistItem>> GetEffectivePageAsync(
        CurrentAccountContext account,
        PageRequest paging,
        IReadOnlyCollection<WatchlistMediaType> visibleTypes,
        CancellationToken cancellationToken,
        WatchlistIdentity? target = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(paging);
        ArgumentNullException.ThrowIfNull(visibleTypes);

        var profileId = account.ProfileId;
        ValidateProfile(profileId);
        var mediaTypes = visibleTypes.Select(WatchlistMediaTypeNames.ToStorage).Distinct().ToArray();

        return await WithConnectionAsync(async connection =>
        {
            var items = new List<WatchlistItem>(paging.PageSize);
            long? totalCount = null;

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = ReadEffectivePageSql;
                foreach (var parameter in SqlParams.Create()
                    .Add("ProfileId", profileId)
                    .Add("VisibleMediaTypes", mediaTypes, NpgsqlDbType.Array | NpgsqlDbType.Text)
                    .Add("TargetMediaType", target is null
                        ? (string?)null
                        : WatchlistMediaTypeNames.ToStorage(target.MediaType))
                    .Add("TargetProvider", target?.ProviderKey)
                    .Add("TargetExternalId", target?.ExternalKey)
                    .ToArray())
                {
                    command.Parameters.Add(parameter);
                }

                foreach (var parameter in paging.ToSqlParameters())
                {
                    command.Parameters.Add(parameter);
                }

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    items.Add(ReadItem(reader, reader.GetBoolean(12)));
                    totalCount ??= reader.GetInt64(13);
                }
            }

            if (totalCount is null)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = CountEffectiveSql;
                foreach (var parameter in SqlParams.Create()
                    .Add("ProfileId", profileId)
                    .Add("VisibleMediaTypes", mediaTypes, NpgsqlDbType.Array | NpgsqlDbType.Text)
                    .Add("TargetMediaType", target is null
                        ? (string?)null
                        : WatchlistMediaTypeNames.ToStorage(target.MediaType))
                    .Add("TargetProvider", target?.ProviderKey)
                    .Add("TargetExternalId", target?.ExternalKey)
                    .ToArray())
                {
                    command.Parameters.Add(parameter);
                }

                totalCount = Convert.ToInt64(
                    await command.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture);
            }

            return PageResult<WatchlistItem>.From(
                items,
                paging,
                totalCount,
                paging.Offset + items.Count < totalCount.Value);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<WatchlistItem>> GetEffectiveAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        ValidateProfile(profileId);
        return await WithConnectionAsync(async connection =>
        {
            var items = new Dictionary<string, WatchlistItem>(StringComparer.Ordinal);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT m."MediaType", m."Provider", m."ExternalId", m."Title", m."NativeTitle",
                           m."CoverImageUrl", m."Format", m."Status", m."Year", f."Id", f."Title", NULL
                    FROM "FranchiseMembers" m
                    INNER JOIN "ProfileFranchiseFollows" pf ON pf."FranchiseId" = m."FranchiseId"
                    INNER JOIN "Franchises" f ON f."Id" = m."FranchiseId"
                    WHERE pf."ProfileId" = @profile
                    ORDER BY f."Title", m."Title";
                    """;
                Add(command, "@profile", profileId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var item = ReadItem(reader, isExplicit: false);
                    items[item.Identity.Key] = item;
                }
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT "MediaType", "Provider", "ExternalId", "Title", "NativeTitle",
                           "CoverImageUrl", "Format", "Status", "Year", NULL, NULL, "UpdatedAtUtc"
                    FROM "ProfileWatchlistPreferences"
                    WHERE "ProfileId" = @profile AND "FollowState" = 'follow'
                    ORDER BY "UpdatedAtUtc" DESC;
                    """;
                Add(command, "@profile", profileId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var item = ReadItem(reader, isExplicit: true);
                    if (items.TryGetValue(item.Identity.Key, out var inherited))
                    {
                        item = item with
                        {
                            FranchiseId = inherited.FranchiseId,
                            FranchiseTitle = inherited.FranchiseTitle
                        };
                    }

                    items[item.Identity.Key] = item;
                }
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT "MediaType", "Provider", "ExternalId"
                    FROM "ProfileWatchlistPreferences"
                    WHERE "ProfileId" = @profile AND "FollowState" = 'ignore';
                    """;
                Add(command, "@profile", profileId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var type = WatchlistMediaTypeNames.Parse(reader.GetString(0));
                    if (type is null)
                    {
                        continue;
                    }

                    var identity = new WatchlistIdentity(type.Value, reader.GetString(1), reader.GetString(2));
                    items.Remove(identity.Key);
                }
            }

            return items.Values
                .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<WatchlistItem>> GetEffectiveAcrossProfilesAsync(
        CancellationToken cancellationToken)
    {
        var profiles = await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT DISTINCT "ProfileId" FROM "ProfileWatchlistPreferences"
                UNION
                SELECT DISTINCT "ProfileId" FROM "ProfileFranchiseFollows";
                """;
            var result = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(reader.GetString(0));
            }

            return result.ToArray();
        }, cancellationToken);

        var items = new Dictionary<string, WatchlistItem>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            foreach (var item in await GetEffectiveAsync(profile, cancellationToken))
            {
                items[item.Identity.Key] = item;
            }
        }

        return items.Values.ToArray();
    }

    public async Task<HashSet<string>> GetEffectiveKeysAsync(
        string profileId,
        CancellationToken cancellationToken) =>
        (await GetEffectiveAsync(profileId, cancellationToken))
            .Select(item => item.Identity.Key)
            .ToHashSet(StringComparer.Ordinal);

    private async Task UpsertPreferenceAsync(
        string profileId,
        WatchlistDraft draft,
        WatchPreferenceState state,
        CancellationToken cancellationToken)
    {
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "ProfileWatchlistPreferences"
                    ("ProfileId", "MediaType", "Provider", "ExternalId", "FollowState", "Title",
                     "NativeTitle", "CoverImageUrl", "Format", "Status", "Year", "UpdatedAtUtc")
                VALUES
                    (@profile, @mediaType, @provider, @externalId, @state, @title,
                     @nativeTitle, @cover, @format, @status, @year, @updated)
                ON CONFLICT ("ProfileId", "MediaType", "Provider", "ExternalId") DO UPDATE SET
                    "FollowState" = excluded."FollowState",
                    "Title" = excluded."Title",
                    "NativeTitle" = excluded."NativeTitle",
                    "CoverImageUrl" = excluded."CoverImageUrl",
                    "Format" = excluded."Format",
                    "Status" = excluded."Status",
                    "Year" = excluded."Year",
                    "UpdatedAtUtc" = excluded."UpdatedAtUtc";
                """;
            Add(command, "@profile", profileId);
            Add(command, "@mediaType", WatchlistMediaTypeNames.ToStorage(draft.Identity.MediaType));
            Add(command, "@provider", draft.Identity.ProviderKey);
            Add(command, "@externalId", draft.Identity.ExternalKey);
            Add(command, "@state", state == WatchPreferenceState.Follow ? "follow" : "ignore");
            Add(command, "@title", draft.Title.Trim());
            Add(command, "@nativeTitle", Clean(draft.NativeTitle));
            Add(command, "@cover", Clean(draft.CoverImageUrl));
            Add(command, "@format", Clean(draft.Format));
            Add(command, "@status", Clean(draft.Status));
            Add(command, "@year", draft.Year);
            Add(command, "@updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    private async Task<bool> IsIncludedByFollowedFranchiseAsync(
        string profileId,
        WatchlistIdentity identity,
        CancellationToken cancellationToken) =>
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT 1
                FROM "FranchiseMembers" m
                INNER JOIN "ProfileFranchiseFollows" pf ON pf."FranchiseId" = m."FranchiseId"
                WHERE pf."ProfileId" = @profile
                  AND m."MediaType" = @mediaType
                  AND m."Provider" = @provider
                  AND m."ExternalId" = @externalId
                LIMIT 1;
                """;
            Add(command, "@profile", profileId);
            Add(command, "@mediaType", WatchlistMediaTypeNames.ToStorage(identity.MediaType));
            Add(command, "@provider", identity.ProviderKey);
            Add(command, "@externalId", identity.ExternalKey);
            return await command.ExecuteScalarAsync(cancellationToken) is not null;
        }, cancellationToken);

    private static WatchlistItem ReadItem(DbDataReader reader, bool isExplicit)
    {
        var type = WatchlistMediaTypeNames.Parse(reader.GetString(0))
            ?? throw new InvalidOperationException("Unknown watchlist media type.");
        var identity = new WatchlistIdentity(type, reader.GetString(1), reader.GetString(2));
        Guid? franchiseId = reader.IsDBNull(9) || !Guid.TryParse(reader.GetString(9), out var parsedFranchise)
            ? null
            : parsedFranchise;
        DateTime? addedAtUtc = reader.IsDBNull(11)
            ? null
            : DateTime.SpecifyKind(
                DateTime.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeKind.Utc);

        // Library entry and link are resolved by WatchlistLibraryResolver when the item is shown.
        return new WatchlistItem(
            identity,
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            null,
            null,
            franchiseId,
            reader.IsDBNull(10) ? null : reader.GetString(10),
            isExplicit,
            addedAtUtc);
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

    private Task WithConnectionAsync(
        Func<DbConnection, Task> action,
        CancellationToken cancellationToken) =>
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

    private static void Validate(string profileId, WatchlistDraft draft)
    {
        ValidateProfile(profileId);
        if (string.IsNullOrWhiteSpace(draft.Title) ||
            string.IsNullOrWhiteSpace(draft.Identity.Provider) ||
            string.IsNullOrWhiteSpace(draft.Identity.ExternalId))
        {
            throw new ArgumentException("A watchlist target needs provider identity and title.", nameof(draft));
        }
    }

    private static void ValidateProfile(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) || profileId.Length > 80)
        {
            throw new ArgumentException("Invalid profile id.", nameof(profileId));
        }
    }
}
