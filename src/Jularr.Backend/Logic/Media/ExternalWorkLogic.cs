using System.Text.RegularExpressions;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Logic.Instance;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    // The provider catalog Discover keeps: every title it loaded once, the current trending lists in their order.
    internal static class ExternalWorkLogic
    {
        // With the DiscoverCache module Discover keeps what it loaded and answers from it; without it Discover only asks the providers.
        public static async Task<bool> KeepsDiscoverTitlesAsync(this LogicContext ctx, CancellationToken ct) =>
            (await ctx.ReadModuleStatesAsync(new InstanceModuleParameters(["DiscoverCache"]), ct)).Any(module => module.IsEnabled);

        // Trending lists change slowly; within this time the catalog answers instead of the provider.
        private static readonly TimeSpan TrendingLifetime = TimeSpan.FromHours(6);

        public static bool IsFresh(IReadOnlyList<ExternalWorkData> trending, DateTimeOffset now) =>
            trending.Count > 0 && now - trending.Max(work => new DateTimeOffset(work.FetchedAt, TimeSpan.Zero)) < TrendingLifetime;

        // Returns how many titles were saved.
        public static async Task<int> SaveExternalWorksAsync(this LogicContext ctx, ExternalWorkBatchData data, CancellationToken ct)
        {
            if (data.Values.Length == 0) return 0;

            // A new trending list replaces the old order of its type.
            var sql = new SqlStatement();
            if (data.IsTrending)
            {
                sql.Statement =
                    """
                    UPDATE
                        "ExternalWorks" AS work
                    SET
                        "TrendingRank" = NULL
                    WHERE
                        work."Namespace" = @Namespace
                        AND work."MediaTypeId" = @MediaTypeId
                        AND work."MediaSubtypeId" IS NOT DISTINCT FROM @MediaSubtypeId::smallint
                        AND work."TrendingRank" IS NOT NULL;
                    """;
                sql.Parameters.Add(data);
                await ctx.ExecuteAsyncForAffectedRows(sql, ct);
                sql = new SqlStatement();
            }

            // A list or search that knows less than an earlier read keeps what was known; only secure image addresses are kept.
            sql.Statement =
                """
                INSERT INTO
                    "ExternalWorks" AS work ("Namespace", "Value", "MediaTypeId", "MediaSubtypeId", "Title", "OriginalTitle", "ReleaseYear", "Overview", "Creator", "ImageUrl", "BackdropUrl", "TrendingRank", "FetchedAt")
                SELECT DISTINCT ON (item."Value")
                    @Namespace,
                    item."Value",
                    @MediaTypeId,
                    @MediaSubtypeId::smallint,
                    item."Title",
                    item."OriginalTitle",
                    nullif(item."ReleaseYear", 0),
                    item."Overview",
                    item."Creator",
                    CASE WHEN item."ImageUrl" ~ '^https://' AND length(item."ImageUrl") <= 500 THEN item."ImageUrl" END,
                    CASE WHEN item."BackdropUrl" ~ '^https://' AND length(item."BackdropUrl") <= 500 THEN item."BackdropUrl" END,
                    CASE WHEN @IsTrending THEN item."Rank"::integer END,
                    now()
                FROM
                    unnest(@Values, @Titles, @OriginalTitles, @ReleaseYears, @Overviews, @Creators, @ImageUrls, @BackdropUrls) WITH ORDINALITY
                        AS item("Value", "Title", "OriginalTitle", "ReleaseYear", "Overview", "Creator", "ImageUrl", "BackdropUrl", "Rank")
                ORDER BY
                    item."Value",
                    item."Rank"
                ON CONFLICT ("Namespace", "Value") DO UPDATE
                SET
                    "MediaTypeId" = EXCLUDED."MediaTypeId",
                    "MediaSubtypeId" = EXCLUDED."MediaSubtypeId",
                    "Title" = EXCLUDED."Title",
                    "OriginalTitle" = coalesce(EXCLUDED."OriginalTitle", work."OriginalTitle"),
                    "ReleaseYear" = coalesce(EXCLUDED."ReleaseYear", work."ReleaseYear"),
                    "Overview" = coalesce(EXCLUDED."Overview", work."Overview"),
                    "Creator" = coalesce(EXCLUDED."Creator", work."Creator"),
                    "ImageUrl" = coalesce(EXCLUDED."ImageUrl", work."ImageUrl"),
                    "BackdropUrl" = coalesce(EXCLUDED."BackdropUrl", work."BackdropUrl"),
                    "TrendingRank" = coalesce(EXCLUDED."TrendingRank", work."TrendingRank"),
                    "FetchedAt" = now();
                """;
            sql.Parameters.Add(data);
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // The current trending list of a type in its order, with when it was loaded.
        public static async Task<IReadOnlyList<ExternalWorkData>> ReadTrendingExternalWorksAsync(this LogicContext ctx, ExternalWorkListData list, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Namespace",
                    work."Value",
                    work."Title",
                    work."OriginalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    work."Overview",
                    work."Creator",
                    work."ImageUrl",
                    work."BackdropUrl",
                    work."Rating",
                    work."FetchedAt"
                FROM
                    "ExternalWorks" AS work
                WHERE
                    work."Namespace" = @Namespace
                    AND work."MediaTypeId" = @MediaTypeId
                    AND work."MediaSubtypeId" IS NOT DISTINCT FROM @MediaSubtypeId
                    AND work."TrendingRank" IS NOT NULL
                ORDER BY
                    work."TrendingRank";
                """;
            sql.Parameters.Add(list);
            return await ctx.ReadSql.ReadListAsync<ExternalWorkData>(sql, ct);
        }
        // A full sync reads the provider's id list once a day and its changes every six hours.
        public static readonly TimeSpan ChangesInterval = TimeSpan.FromHours(6);

        // Jularr counts as used while someone made a request within these minutes.
        public static readonly TimeSpan UseLasts = TimeSpan.FromMinutes(5);

        public static bool IsListDue(ExternalWorkSyncData sync, DateTimeOffset now) => sync.ListedOn != DateOnly.FromDateTime(now.UtcDateTime) && now.UtcDateTime.Hour >= 9;

        // A catalog without a daily id list walks all its titles; an unfinished walk goes on, a finished one starts again after a week.
        public static readonly TimeSpan FullPassInterval = TimeSpan.FromDays(7);

        public static bool IsFullPassDue(ExternalWorkSyncData sync, DateTimeOffset now) =>
            sync.Cursor is not null || sync.ListedOn is not { } listed || DateOnly.FromDateTime(now.UtcDateTime).DayNumber - listed.DayNumber >= FullPassInterval.Days;

        public static bool AreChangesDue(ExternalWorkSyncData sync, DateTimeOffset now) =>
            sync.ChangesReadAt is not { } read || now - new DateTimeOffset(read, TimeSpan.Zero) >= ChangesInterval;

        // A catalog syncs when the Owner kept it on, inside one of its windows (none: any time) and, if it waits, while nobody uses Jularr.
        public static bool MaySync(ExternalWorkSyncData sync, IReadOnlyList<ExternalWorkSyncWindowData> windows, TimeOnly localTime, bool isUsed)
        {
            if (!sync.IsEnabled || sync.PausesWhileUsed && isUsed) return false;
            var own = windows.Where(window => window.Namespace == sync.Namespace).ToArray();
            return own.Length == 0 || own.Any(window => window.StartsAt < window.EndsAt
                ? localTime >= window.StartsAt && localTime < window.EndsAt
                : localTime >= window.StartsAt || localTime < window.EndsAt);
        }

        // Discover's lists of a whole catalog: trending in its order, top by rating among titles enough people rated,
        // new as the most popular of the last weeks and upcoming as the most popular of the coming months.
        public const int ShelfSize = 20;
        public const int TopVotes = 300;
        public const int NewWithinDays = 60;
        public const int UpcomingWithinDays = 180;
        public const int SuggestionCount = 6;

        // With full local discovery a catalog read whole answers Discover itself.
        public static async Task<IReadOnlySet<string>> ReadWholeExternalWorkCatalogsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var modules = await ctx.ReadModuleStatesAsync(new InstanceModuleParameters(["DiscoverCache", "LocalDiscovery"]), ct);
            if (modules.Count(module => module.IsEnabled) < 2) return new HashSet<string>(StringComparer.Ordinal);

            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    sync."Namespace"
                FROM
                    "ExternalWorkSyncs" AS sync
                WHERE
                    sync."IsEnabled"
                    AND sync."CompletedAt" IS NOT NULL;
                """;
            return (await ctx.ReadSql.ReadListAsync<ExternalWorkNamespaceData>(sql, ct)).Select(row => row.Namespace).ToHashSet(StringComparer.Ordinal);
        }

        // Every shelf in one statement, each in its own order; the Shelf number keeps them apart.
        public static async Task<IReadOnlyList<ExternalWorkShelfItemData>> ReadExternalWorkShelvesAsync(this LogicContext ctx, ExternalWorkShelfQueryData data, CancellationToken ct)
        {
            // Feed numbers as DiscoverFeedType: 1 trending, 2 top, 3 new, 4 upcoming; only the shelf's own list is read, in the order of its index.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    shelf."Shelf",
                    work."Namespace",
                    work."Value",
                    work."Title",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    work."Overview",
                    work."Creator",
                    coalesce(known."StoredImageUrl", work."ImageUrl") AS "ImageUrl",
                    work."BackdropUrl",
                    work."Rating",
                    known."WorkId",
                    known."MediaStateTypeId",
                    known."PlexUrl"
                FROM
                    jsonb_to_recordset(@Shelves::jsonb) AS shelf ("Shelf" integer, "Namespace" text, "MediaTypeId" smallint, "MediaSubtypeId" smallint, "Feed" smallint)
                    CROSS JOIN LATERAL unnest(CASE shelf."Feed"
                        WHEN 1 THEN ARRAY(
                            SELECT work."Id" FROM "ExternalWorks" AS work
                            WHERE work."Namespace" = shelf."Namespace" AND work."MediaTypeId" = shelf."MediaTypeId" AND coalesce(work."MediaSubtypeId", 0) = shelf."MediaSubtypeId"
                                AND NOT work."IsAdult" AND work."ImageUrl" IS NOT NULL AND work."TrendingRank" IS NOT NULL
                            ORDER BY work."TrendingRank", work."Id"
                            LIMIT @Limit OFFSET @Offset)
                        WHEN 2 THEN ARRAY(
                            SELECT work."Id" FROM "ExternalWorks" AS work
                            WHERE work."Namespace" = shelf."Namespace" AND work."MediaTypeId" = shelf."MediaTypeId" AND coalesce(work."MediaSubtypeId", 0) = shelf."MediaSubtypeId"
                                AND NOT work."IsAdult" AND work."ImageUrl" IS NOT NULL AND work."Votes" >= @TopVotes
                            ORDER BY work."Rating" DESC NULLS LAST, work."Votes" DESC, work."Id"
                            LIMIT @Limit OFFSET @Offset)
                        WHEN 3 THEN ARRAY(
                            SELECT work."Id" FROM "ExternalWorks" AS work
                            WHERE work."Namespace" = shelf."Namespace" AND work."MediaTypeId" = shelf."MediaTypeId" AND coalesce(work."MediaSubtypeId", 0) = shelf."MediaSubtypeId"
                                AND NOT work."IsAdult" AND work."ImageUrl" IS NOT NULL AND work."ReleasedOn" BETWEEN @NewSince AND @Today
                            ORDER BY work."Popularity" DESC NULLS LAST, work."Id"
                            LIMIT @Limit OFFSET @Offset)
                        WHEN 4 THEN ARRAY(
                            SELECT work."Id" FROM "ExternalWorks" AS work
                            WHERE work."Namespace" = shelf."Namespace" AND work."MediaTypeId" = shelf."MediaTypeId" AND coalesce(work."MediaSubtypeId", 0) = shelf."MediaSubtypeId"
                                AND NOT work."IsAdult" AND work."ImageUrl" IS NOT NULL AND work."ReleasedOn" > @Today AND work."ReleasedOn" <= @UpcomingUntil
                            ORDER BY work."Popularity" DESC NULLS LAST, work."Id"
                            LIMIT @Limit OFFSET @Offset)
                    END) WITH ORDINALITY AS picked ("Id", "Place")
                    JOIN "ExternalWorks" AS work ON work."Id" = picked."Id"
                    LEFT JOIN LATERAL (
                        SELECT
                            visible."WorkId",
                            state."MediaStateTypeId",
                            (
                                SELECT
                                    fact."Value"
                                FROM
                                    "WorkMetadataFacts" AS fact
                                WHERE
                                    fact."WorkId" = visible."WorkId"
                                    AND fact."FactKey" = 'Poster'
                                    AND fact."Value" LIKE '/api/v1/covers/%'
                                    AND EXISTS (SELECT 1 FROM "WorkExternalIdentities" AS own WHERE own."WorkId" = visible."WorkId" AND own."Namespace" = work."Namespace" AND own."Value" = work."Value")
                                ORDER BY
                                    fact."IsManual" DESC
                                LIMIT 1
                            ) AS "StoredImageUrl",
                            (
                                SELECT
                                    'https://app.plex.tv/desktop/#!/server/' || provider."ExternalId" || '/details?key=%2Flibrary%2Fmetadata%2F' || plex."Value"
                                FROM
                                    "WorkExternalIdentities" AS plex
                                    JOIN "Providers" AS provider ON provider."Key" = 'plex' AND provider."IsEnabled" AND provider."ExternalId" IS NOT NULL
                                WHERE
                                    plex."WorkId" = visible."WorkId"
                                    AND plex."Namespace" = 'plex'
                                LIMIT 1
                            ) AS "PlexUrl"
                        FROM
                            "ProfileWorks" AS visible
                            LEFT JOIN "ProfileWorkStates" AS state ON state."ProfileId" = visible."ProfileId" AND state."WorkId" = visible."WorkId"
                        WHERE
                            visible."ProfileId" = @ProfileId
                            AND visible."WorkId" IN (
                                SELECT
                                    identity."WorkId"
                                FROM
                                    "WorkExternalIdentities" AS identity
                                WHERE
                                    identity."Namespace" = work."Namespace"
                                    AND identity."Value" = work."Value"
                                UNION ALL
                                SELECT
                                    unit."WorkId"
                                FROM
                                    "WorkUnitExternalIdentities" AS identity
                                    JOIN "WorkUnits" AS unit ON unit."Id" = identity."WorkUnitId"
                                WHERE
                                    identity."Namespace" = work."Namespace"
                                    AND identity."Value" = work."Value"
                            )
                        LIMIT 1
                    ) AS known ON TRUE
                ORDER BY
                    shelf."Shelf",
                    picked."Place";
                """;
            sql.Parameters.Add(data);
            sql.Parameters.Add(new ExternalWorkRankingData(ShelfSize, TopVotes, data.Today.AddDays(-NewWithinDays), data.Today.AddDays(UpcomingWithinDays)));
            return await ctx.ReadSql.ReadListAsync<ExternalWorkShelfItemData>(sql, ct);
        }

        // Typed words as a prefix search: every word has to begin a word of the title.
        public static string? SearchTerms(string query)
        {
            var words = Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(match => match.Value).Take(8).ToArray();
            return words.Length == 0 ? null : string.Join(" & ", words.Select(word => word + ":*"));
        }

        // A title that begins with the typed text ranks higher; LIKE's own characters count literally.
        public static string TitlePrefix(string query) =>
            query.Trim().ToLowerInvariant().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

        // Suggestions while typing: titles in the profile's library first, then trending ones, titles that begin with the text,
        // and the most popular; each catalog ranks its own titles, a title two catalogs know shows once.
        public static async Task<IReadOnlyList<ExternalWorkShelfItemData>> SuggestExternalWorksAsync(this LogicContext ctx, ExternalWorkSuggestionQueryData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH candidate AS (
                    SELECT
                        scope."Shelf",
                        work."Namespace",
                        work."Value",
                        work."ImdbId",
                        row_number() OVER (
                            PARTITION BY work."Namespace"
                            ORDER BY work."TrendingRank" IS NULL, lower(work."Title") LIKE @TitlePrefix DESC, work."Popularity" DESC NULLS LAST, work."Id"
                        ) AS "Place"
                    FROM
                        "ExternalWorks" AS work
                        JOIN jsonb_to_recordset(@Shelves::jsonb) AS scope ("Shelf" integer, "Namespace" text, "MediaTypeId" smallint, "MediaSubtypeId" smallint)
                            ON scope."Namespace" = work."Namespace" AND scope."MediaTypeId" = work."MediaTypeId" AND scope."MediaSubtypeId" = coalesce(work."MediaSubtypeId", 0)
                    WHERE
                        work."SearchText" @@ to_tsquery('simple', @SearchTerms)
                        AND NOT work."IsAdult"
                        AND work."ImageUrl" IS NOT NULL
                )
                SELECT
                    found."Shelf",
                    found."Namespace",
                    found."Value",
                    found."Title",
                    found."ReleaseYear",
                    found."Overview",
                    found."Creator",
                    found."ImageUrl",
                    found."BackdropUrl",
                    found."Rating",
                    found."WorkId",
                    found."MediaStateTypeId",
                    found."PlexUrl"
                FROM
                    (
                        SELECT DISTINCT ON (coalesce(candidate."ImdbId", candidate."Namespace" || ':' || candidate."Value"))
                            candidate."Shelf",
                            candidate."Place",
                            work."Namespace",
                            work."Value",
                            work."Title",
                            work."ReleaseYear"::integer AS "ReleaseYear",
                            work."Overview",
                            work."Creator",
                            coalesce(known."StoredImageUrl", work."ImageUrl") AS "ImageUrl",
                            work."BackdropUrl",
                            work."Rating",
                            known."WorkId",
                            known."MediaStateTypeId",
                            known."PlexUrl"
                        FROM
                            candidate
                            JOIN "ExternalWorks" AS work ON work."Namespace" = candidate."Namespace" AND work."Value" = candidate."Value"
                            LEFT JOIN LATERAL (
                                SELECT
                                    visible."WorkId",
                                    state."MediaStateTypeId",
                                    (
                                        SELECT
                                            fact."Value"
                                        FROM
                                            "WorkMetadataFacts" AS fact
                                        WHERE
                                            fact."WorkId" = visible."WorkId"
                                            AND fact."FactKey" = 'Poster'
                                            AND fact."Value" LIKE '/api/v1/covers/%'
                                            AND EXISTS (SELECT 1 FROM "WorkExternalIdentities" AS own WHERE own."WorkId" = visible."WorkId" AND own."Namespace" = work."Namespace" AND own."Value" = work."Value")
                                        ORDER BY
                                            fact."IsManual" DESC
                                        LIMIT 1
                                    ) AS "StoredImageUrl",
                                    (
                                        SELECT
                                            'https://app.plex.tv/desktop/#!/server/' || provider."ExternalId" || '/details?key=%2Flibrary%2Fmetadata%2F' || plex."Value"
                                        FROM
                                            "WorkExternalIdentities" AS plex
                                            JOIN "Providers" AS provider ON provider."Key" = 'plex' AND provider."IsEnabled" AND provider."ExternalId" IS NOT NULL
                                        WHERE
                                            plex."WorkId" = visible."WorkId"
                                            AND plex."Namespace" = 'plex'
                                        LIMIT 1
                                    ) AS "PlexUrl"
                                FROM
                                    "ProfileWorks" AS visible
                                    LEFT JOIN "ProfileWorkStates" AS state ON state."ProfileId" = visible."ProfileId" AND state."WorkId" = visible."WorkId"
                                WHERE
                                    visible."ProfileId" = @ProfileId
                                    AND visible."WorkId" IN (
                                        SELECT
                                            identity."WorkId"
                                        FROM
                                            "WorkExternalIdentities" AS identity
                                        WHERE
                                            identity."Namespace" = work."Namespace"
                                            AND identity."Value" = work."Value"
                                        UNION ALL
                                        SELECT
                                            unit."WorkId"
                                        FROM
                                            "WorkUnitExternalIdentities" AS identity
                                            JOIN "WorkUnits" AS unit ON unit."Id" = identity."WorkUnitId"
                                        WHERE
                                            identity."Namespace" = work."Namespace"
                                            AND identity."Value" = work."Value"
                                    )
                                LIMIT 1
                            ) AS known ON TRUE
                        WHERE
                            candidate."Place" <= @Limit
                        ORDER BY
                            coalesce(candidate."ImdbId", candidate."Namespace" || ':' || candidate."Value"),
                            known."WorkId" IS NULL,
                            candidate."Place"
                    ) AS found
                ORDER BY
                    found."WorkId" IS NULL,
                    found."Place",
                    found."Shelf"
                LIMIT @Limit;
                """;
            sql.Parameters.Add(data);
            return await ctx.ReadSql.ReadListAsync<ExternalWorkShelfItemData>(sql, ct);
        }

        public static async Task<ExternalWorkSyncData?> ReadExternalWorkSyncAsync(this LogicContext ctx, string @namespace, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    sync."Namespace",
                    sync."IsEnabled",
                    sync."RequestsPerMinute"::integer AS "RequestsPerMinute",
                    sync."PausesWhileUsed",
                    sync."ListedOn",
                    sync."Cursor",
                    sync."ChangesReadAt"
                FROM
                    "ExternalWorkSyncs" AS sync
                WHERE
                    sync."Namespace" = @Namespace;
                """;
            sql.Parameters.Add(new ExternalWorkSyncProgressData(@namespace, null, null, null));
            return await ctx.ReadSql.ReadOptionalAsync<ExternalWorkSyncData>(sql, ct);
        }

        public static async Task<IReadOnlyList<ExternalWorkSyncWindowData>> ReadExternalWorkSyncWindowsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    slot."Namespace",
                    slot."StartsAt",
                    slot."EndsAt"
                FROM
                    "ExternalWorkSyncWindows" AS slot
                ORDER BY
                    slot."Namespace",
                    slot."StartsAt";
                """;
            return await ctx.ReadSql.ReadListAsync<ExternalWorkSyncWindowData>(sql, ct);
        }

        // The kept catalogs with how many titles they hold and how many still wait to be read.
        public static async Task<IReadOnlyList<ExternalWorkSyncSummaryData>> ReadExternalWorkSyncSummariesAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    sync."Namespace",
                    sync."IsEnabled",
                    sync."RequestsPerMinute"::integer AS "RequestsPerMinute",
                    sync."PausesWhileUsed",
                    sync."ListedOn",
                    (SELECT count(*) FROM "ExternalWorks" AS work WHERE work."Namespace" = sync."Namespace") AS "Entries",
                    (SELECT count(*) FROM "ExternalWorks" AS work WHERE work."Namespace" = sync."Namespace" AND work."DetailedAt" IS NULL) AS "Pending",
                    sync."Version"
                FROM
                    "ExternalWorkSyncs" AS sync
                ORDER BY
                    sync."Namespace";
                """;
            return await ctx.ReadSql.ReadListAsync<ExternalWorkSyncSummaryData>(sql, ct);
        }

        // Progress of a pass; it leaves the Owner's choices and their version alone.
        public static async Task<int> SaveExternalWorkSyncAsync(this LogicContext ctx, ExternalWorkSyncProgressData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ExternalWorkSyncs" AS sync
                SET
                    "ListedOn" = @ListedOn,
                    "Cursor" = @Cursor,
                    "ChangesReadAt" = @ChangesReadAt,
                    "UpdatedAt" = now()
                WHERE
                    sync."Namespace" = @Namespace;
                """;
            sql.Parameters.Add(data);
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // The first time a catalog was read whole; later passes keep the date.
        public static async Task<int> CompleteExternalWorkSyncAsync(this LogicContext ctx, string @namespace, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ExternalWorkSyncs" AS sync
                SET
                    "CompletedAt" = now(),
                    "UpdatedAt" = now()
                WHERE
                    sync."Namespace" = @Namespace
                    AND sync."CompletedAt" IS NULL;
                """;
            sql.Parameters.Add(new ExternalWorkSyncProgressData(@namespace, null, null, null));
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // The Owner's choices replace the old ones and their windows, if nobody changed them in between.
        public static async Task UpdateExternalWorkSyncSettingsAsync(this LogicContext ctx, ExternalWorkSyncSettingsData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ExternalWorkSyncs" AS sync
                SET
                    "IsEnabled" = @IsEnabled,
                    "RequestsPerMinute" = @RequestsPerMinute,
                    "PausesWhileUsed" = @PausesWhileUsed,
                    "Version" = sync."Version" + 1,
                    "UpdatedAt" = now()
                WHERE
                    sync."Namespace" = @Namespace
                    AND sync."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "invalid_transition");
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                WITH removed AS (
                    DELETE FROM
                        "ExternalWorkSyncWindows" AS slot
                    WHERE
                        slot."Namespace" = @Namespace
                )
                INSERT INTO
                    "ExternalWorkSyncWindows" ("Namespace", "StartsAt", "EndsAt")
                SELECT
                    @Namespace,
                    slot."StartsAt",
                    slot."EndsAt"
                FROM
                    jsonb_to_recordset(@Windows::jsonb) AS slot ("StartsAt" time(0), "EndsAt" time(0));
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // A provider's id list adds the titles not yet known; known ones only take its popularity, their titles stay.
        public static async Task<int> ListExternalWorksAsync(this LogicContext ctx, ExternalWorkEntriesData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ExternalWorks" AS work ("Namespace", "Value", "MediaTypeId", "MediaSubtypeId", "Title", "OriginalTitle", "Popularity", "FetchedAt")
                SELECT DISTINCT ON (entry."Value")
                    @Namespace,
                    entry."Value",
                    @MediaTypeId,
                    @MediaSubtypeId::smallint,
                    left(entry."Title", 500),
                    left(entry."Title", 500),
                    greatest(entry."Popularity", 0),
                    now()
                FROM
                    jsonb_to_recordset(@Entries::jsonb) AS entry ("Value" text, "Title" text, "Popularity" real)
                WHERE
                    length(btrim(entry."Title")) > 0
                ORDER BY
                    entry."Value"
                ON CONFLICT ("Namespace", "Value") DO UPDATE
                SET
                    "Popularity" = EXCLUDED."Popularity"
                WHERE
                    work."Popularity" IS DISTINCT FROM EXCLUDED."Popularity";
                """;
            sql.Parameters.Add(data);
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task<IReadOnlyList<string>> ReadUndetailedExternalWorksAsync(this LogicContext ctx, ExternalWorkQueueData queue, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Value"
                FROM
                    "ExternalWorks" AS work
                WHERE
                    work."Namespace" = @Namespace
                    AND work."DetailedAt" IS NULL
                ORDER BY
                    work."Popularity" DESC NULLS LAST,
                    work."Id"
                LIMIT @Limit;
                """;
            sql.Parameters.Add(queue);
            return [.. (await ctx.ReadSql.ReadListAsync<ExternalWorkValueData>(sql, ct)).Select(row => row.Value)];
        }

        // Entries just read replace what the catalog knew; entries the provider no longer has leave it.
        public static async Task<int> DetailExternalWorksAsync(this LogicContext ctx, ExternalWorkEntriesData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "ExternalWorks" AS work
                WHERE
                    work."Namespace" = @Namespace
                    AND work."Value" = ANY(@Gone);
                """;
            sql.Parameters.Add(data);
            var changed = await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ExternalWorks" AS work
                SET
                    "Title" = left(entry."Title", 500),
                    "OriginalTitle" = left(entry."OriginalTitle", 500),
                    "ReleasedOn" = entry."ReleasedOn",
                    "ReleaseYear" = CASE WHEN extract(year FROM entry."ReleasedOn") BETWEEN 1800 AND 2200 THEN extract(year FROM entry."ReleasedOn")::smallint END,
                    "Overview" = left(entry."Overview", 4000),
                    "ImageUrl" = CASE WHEN entry."ImageUrl" ~ '^https://' AND length(entry."ImageUrl") <= 500 THEN entry."ImageUrl" END,
                    "BackdropUrl" = CASE WHEN entry."BackdropUrl" ~ '^https://' AND length(entry."BackdropUrl") <= 500 THEN entry."BackdropUrl" END,
                    "Popularity" = coalesce(greatest(entry."Popularity", 0), work."Popularity"),
                    "Rating" = CASE WHEN entry."Rating" BETWEEN 0 AND 10 THEN round(entry."Rating"::numeric, 2) END,
                    "Votes" = CASE WHEN entry."Votes" >= 0 THEN entry."Votes" END,
                    "ImdbId" = CASE WHEN entry."ImdbId" ~ '^tt[0-9]{7,10}$' THEN entry."ImdbId" END,
                    "IsAdult" = entry."IsAdult",
                    "FetchedAt" = now(),
                    "DetailedAt" = now()
                FROM
                    jsonb_to_recordset(@Entries::jsonb) AS entry ("Value" text, "Title" text, "OriginalTitle" text, "ReleasedOn" date, "Overview" text, "ImageUrl" text,
                        "BackdropUrl" text, "Popularity" real, "Rating" double precision, "Votes" integer, "ImdbId" text, "IsAdult" boolean)
                WHERE
                    work."Namespace" = @Namespace
                    AND work."Value" = entry."Value"
                    AND length(btrim(entry."Title")) > 0;
                """;
            sql.Parameters.Add(data);
            return changed + await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // Entries the provider changed are read again.
        public static async Task<int> MarkChangedExternalWorksAsync(this LogicContext ctx, ExternalWorkValuesData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ExternalWorks" AS work
                SET
                    "DetailedAt" = NULL
                WHERE
                    work."Namespace" = @Namespace
                    AND work."Value" = ANY(@Values)
                    AND work."DetailedAt" IS NOT NULL;
                """;
            sql.Parameters.Add(data);
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
