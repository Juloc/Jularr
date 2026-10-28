using System.Text.RegularExpressions;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Logic.Instance;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    // The provider catalog Discover keeps: every title it loaded once, the current trending lists in their order.
    internal static class ExternalWorkLogic
    {
        // The one rule for how much of the provider catalogs is kept, for the whole instance: without the Caching module (key DiscoverCache) only the library and
        // opened titles (lean), with it also Discover's lists (standard), with its sub-option LocalDiscovery the whole catalog (full).
        public static CachingTier CachingTierOf(bool caching, bool fullCatalog) => !caching ? CachingTier.Lean : fullCatalog ? CachingTier.Full : CachingTier.Standard;

        public static async Task<CachingTier> ReadCachingTierAsync(this LogicContext ctx, CancellationToken ct)
        {
            var modules = await ctx.ReadModuleStatesAsync(new InstanceModuleParameters(["DiscoverCache", "LocalDiscovery"]), ct);
            return CachingTierOf(modules.Any(module => module.Key == "DiscoverCache" && module.IsEnabled), modules.Any(module => module.Key == "LocalDiscovery" && module.IsEnabled));
        }

        // Catalogs with a full list may be kept whole; on the full tier every other one keeps Discover's lists.
        public static readonly IReadOnlySet<string> FullSyncCatalogs = new HashSet<string>(StringComparer.Ordinal) { "tmdb_movie", "anilist" };

        public static CachingTier CatalogTier(CachingTier tier, string @namespace) => tier == CachingTier.Full && !FullSyncCatalogs.Contains(@namespace) ? CachingTier.Standard : tier;

        // Above the lean tier Discover keeps its lists and answers from them; on the lean tier it asks the provider each time.
        public static async Task<bool> KeepsDiscoverListsAsync(this LogicContext ctx, CancellationToken ct) => await ctx.ReadCachingTierAsync(ct) != CachingTier.Lean;

        // The one owner of catalog rows: every title a list, search or relation names is a Work with its identity, found again by that identity.
        // An anime entry AniList knows as a season of a series is that series with the season, also while an earlier entry of its own still waits to be merged into it.
        // A TheTVDB series a TMDB list or Sonarr named keeps TMDB's and IMDb's ids as well; a series Work known only by that TMDB id is the same Work and takes the TheTVDB id.
        public static async Task<IReadOnlyList<ExternalWorkSavedData>> SaveExternalWorksAsync(this LogicContext ctx, ExternalWorkBatchData data, CancellationToken ct)
        {
            if (data.Values.Length == 0) return [];

            // Saves of one catalog wait for each other, so a title never becomes two Works.
            var sql = new SqlStatement();
            sql.Statement = "SELECT pg_advisory_xact_lock(hashtextextended('catalog-works:' || @Namespace, 0));";
            sql.Parameters.Add(data);
            await ctx.Sql.ExecuteScalarAsync(sql, ct);

            // A new trending list replaces the old order of its type.
            if (data.IsTrending)
            {
                sql = new SqlStatement();
                sql.Statement =
                    """
                    UPDATE
                        "Works" AS work
                    SET
                        "TrendingRank" = NULL
                    WHERE
                        work."CatalogNamespace" = @Namespace
                        AND work."MediaTypeId" = @MediaTypeId
                        AND coalesce(work."MediaSubtypeId", 0) = coalesce(@MediaSubtypeId::smallint, 0)
                        AND work."TrendingRank" IS NOT NULL;
                    """;
                sql.Parameters.Add(data);
                await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            }

            // A list or search that knows less than an earlier read keeps what was known; only secure image addresses are kept.
            // A Work keeps its title, year and subtype once it has details or a person chose its subtype, a list without a subtype never clears one,
            // and another catalog's Work keeps its catalog fields.
            sql = new SqlStatement();
            sql.Statement =
                """
                WITH item AS (
                    SELECT DISTINCT ON (entry."Value")
                        entry."Value",
                        entry."Title",
                        entry."OriginalTitle",
                        nullif(entry."ReleaseYear", 0)::smallint AS "ReleaseYear",
                        entry."Overview",
                        entry."Creator",
                        CASE WHEN entry."ImageUrl" ~ '^https://' AND length(entry."ImageUrl") <= 500 THEN entry."ImageUrl" END AS "ImageUrl",
                        CASE WHEN entry."BackdropUrl" ~ '^https://' AND length(entry."BackdropUrl") <= 500 THEN entry."BackdropUrl" END AS "BackdropUrl",
                        nullif(entry."Rating", 0) AS "Rating",
                        CASE WHEN @IsTrending THEN entry."Rank"::integer END AS "TrendingRank",
                        entry."TmdbValue",
                        entry."ImdbValue"
                    FROM
                        unnest(@Values, @Titles, @OriginalTitles, @ReleaseYears, @Overviews, @Creators, @ImageUrls, @BackdropUrls, @Ratings, @TmdbValues, @ImdbValues) WITH ORDINALITY
                            AS entry ("Value", "Title", "OriginalTitle", "ReleaseYear", "Overview", "Creator", "ImageUrl", "BackdropUrl", "Rating", "TmdbValue", "ImdbValue", "Rank")
                    ORDER BY
                        entry."Value",
                        entry."Rank"
                ), resolved AS (
                    SELECT
                        item."Value",
                        item."TmdbValue",
                        item."ImdbValue",
                        coalesce(identity."WorkId", linked."WorkId") AS "KnownWorkId",
                        linked."WorkId" IS NOT NULL AS "IsLinked",
                        season."WorkId" AS "SeriesWorkId",
                        season."SeasonNumber"
                    FROM
                        item
                        LEFT JOIN "WorkExternalIdentities" AS identity ON identity."Namespace" = @Namespace AND identity."Value" = item."Value"
                        LEFT JOIN LATERAL (
                            SELECT
                                tmdb."WorkId"
                            FROM
                                "WorkExternalIdentities" AS tmdb
                                JOIN "Works" AS work ON work."Id" = tmdb."WorkId" AND work."MediaTypeId" = 2
                            WHERE
                                tmdb."Namespace" = 'tmdb_tv'
                                AND tmdb."Value" = item."TmdbValue"
                                AND NOT EXISTS (SELECT 1 FROM "WorkExternalIdentities" AS own WHERE own."WorkId" = tmdb."WorkId" AND own."Namespace" = @Namespace)
                        ) AS linked ON identity."WorkId" IS NULL
                        LEFT JOIN LATERAL (
                            SELECT
                                unit."WorkId",
                                unit."Number"::integer AS "SeasonNumber"
                            FROM
                                "WorkUnitExternalIdentities" AS mapped
                                JOIN "WorkUnits" AS unit ON unit."Id" = mapped."WorkUnitId" AND unit."WorkUnitTypeId" = 1
                            WHERE
                                mapped."Namespace" = @Namespace
                                AND mapped."Value" = item."Value"
                        ) AS season ON TRUE
                ), updated AS (
                    UPDATE
                        "Works" AS work
                    SET
                        "CatalogNamespace" = @Namespace,
                        "CanonicalTitle" = CASE WHEN work."DetailedAt" IS NULL THEN item."Title" ELSE work."CanonicalTitle" END,
                        "ReleaseYear" = CASE WHEN work."DetailedAt" IS NULL THEN coalesce(item."ReleaseYear", work."ReleaseYear") ELSE work."ReleaseYear" END,
                        "MediaSubtypeId" = CASE
                            WHEN work."DetailedAt" IS NULL AND NOT work."IsMediaSubtypeManual" AND work."MediaTypeId" = @MediaTypeId THEN coalesce(@MediaSubtypeId::smallint, work."MediaSubtypeId")
                            ELSE work."MediaSubtypeId"
                        END,
                        "OriginalTitle" = coalesce(item."OriginalTitle", work."OriginalTitle"),
                        "Overview" = coalesce(item."Overview", work."Overview"),
                        "Creator" = coalesce(item."Creator", work."Creator"),
                        "ImageUrl" = coalesce(item."ImageUrl", work."ImageUrl"),
                        "BackdropUrl" = coalesce(item."BackdropUrl", work."BackdropUrl"),
                        "Rating" = coalesce(item."Rating", work."Rating"),
                        "TrendingRank" = coalesce(item."TrendingRank", work."TrendingRank")
                    FROM
                        resolved
                        JOIN item ON item."Value" = resolved."Value"
                    WHERE
                        work."Id" = resolved."KnownWorkId"
                        AND resolved."KnownWorkId" = coalesce(resolved."SeriesWorkId", resolved."KnownWorkId")
                        AND (work."CatalogNamespace" IS NULL OR work."CatalogNamespace" = @Namespace)
                ), fresh AS (
                    SELECT
                        item."Value",
                        item."Title",
                        item."OriginalTitle",
                        item."ReleaseYear",
                        item."Overview",
                        item."Creator",
                        item."ImageUrl",
                        item."BackdropUrl",
                        item."Rating",
                        item."TrendingRank",
                        nextval(pg_get_serial_sequence('"Works"', 'Id')) AS "WorkId"
                    FROM
                        item
                        JOIN resolved ON resolved."Value" = item."Value"
                    WHERE
                        resolved."KnownWorkId" IS NULL
                        AND resolved."SeriesWorkId" IS NULL
                ), created AS (
                    INSERT INTO
                        "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "OriginalTitle", "ReleaseYear", "Overview", "Creator", "ImageUrl", "BackdropUrl", "Rating",
                            "TrendingRank", "CatalogNamespace")
                    SELECT
                        fresh."WorkId",
                        @MediaTypeId,
                        @MediaSubtypeId::smallint,
                        fresh."Title",
                        fresh."OriginalTitle",
                        fresh."ReleaseYear",
                        fresh."Overview",
                        fresh."Creator",
                        fresh."ImageUrl",
                        fresh."BackdropUrl",
                        fresh."Rating",
                        fresh."TrendingRank",
                        @Namespace
                    FROM
                        fresh
                ), identified AS (
                    INSERT INTO
                        "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                    SELECT
                        fresh."WorkId",
                        @Namespace,
                        fresh."Value"
                    FROM
                        fresh
                ), links AS (
                    INSERT INTO
                        "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                    SELECT
                        resolved."KnownWorkId",
                        @Namespace,
                        resolved."Value"
                    FROM
                        resolved
                    WHERE
                        resolved."IsLinked"
                    UNION ALL
                    SELECT
                        coalesce(resolved."KnownWorkId", fresh."WorkId"),
                        'tmdb_tv',
                        resolved."TmdbValue"
                    FROM
                        resolved
                        LEFT JOIN fresh ON fresh."Value" = resolved."Value"
                    WHERE
                        resolved."TmdbValue" IS NOT NULL
                        AND resolved."SeriesWorkId" IS NULL
                        AND coalesce(resolved."KnownWorkId", fresh."WorkId") IS NOT NULL
                    UNION ALL
                    SELECT
                        coalesce(resolved."KnownWorkId", fresh."WorkId"),
                        'imdb',
                        resolved."ImdbValue"
                    FROM
                        resolved
                        LEFT JOIN fresh ON fresh."Value" = resolved."Value"
                    WHERE
                        resolved."ImdbValue" IS NOT NULL
                        AND resolved."SeriesWorkId" IS NULL
                        AND coalesce(resolved."KnownWorkId", fresh."WorkId") IS NOT NULL
                    ON CONFLICT DO NOTHING
                )
                SELECT
                    resolved."Value",
                    coalesce(resolved."SeriesWorkId", resolved."KnownWorkId") AS "WorkId",
                    resolved."SeasonNumber"
                FROM
                    resolved
                WHERE
                    coalesce(resolved."SeriesWorkId", resolved."KnownWorkId") IS NOT NULL
                UNION ALL
                SELECT
                    fresh."Value",
                    fresh."WorkId",
                    NULL::integer
                FROM
                    fresh;
                """;
            sql.Parameters.Add(data);
            return await ctx.Sql.ReadListAsync<ExternalWorkSavedData>(sql, ct);
        }

        // The current trending list of a type in its order.
        public static async Task<IReadOnlyList<ExternalWorkData>> ReadTrendingExternalWorksAsync(this LogicContext ctx, ExternalWorkListData list, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."CatalogNamespace" AS "Namespace",
                    identity."Value",
                    work."CanonicalTitle" AS "Title",
                    work."OriginalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    work."Overview",
                    work."Creator",
                    work."ImageUrl",
                    work."BackdropUrl",
                    work."FetchedAt",
                    work."Rating",
                    work."Id" AS "WorkId",
                    NULL::integer AS "SeasonNumber"
                FROM
                    "Works" AS work
                    JOIN "WorkExternalIdentities" AS identity ON identity."WorkId" = work."Id" AND identity."Namespace" = work."CatalogNamespace"
                WHERE
                    work."CatalogNamespace" = @Namespace
                    AND work."MediaTypeId" = @MediaTypeId
                    AND coalesce(work."MediaSubtypeId", 0) = coalesce(@MediaSubtypeId::smallint, 0)
                    AND work."TrendingRank" IS NOT NULL
                ORDER BY
                    work."TrendingRank",
                    work."Id"
                LIMIT 500;
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

        // A catalog syncs while it is kept whole, inside one of its windows (none: any time) and, if it waits, while nobody uses Jularr.
        public static bool MaySync(CachingTier tier, ExternalWorkSyncData sync, IReadOnlyList<ExternalWorkSyncWindowData> windows, TimeOnly localTime, bool isUsed)
        {
            if (CatalogTier(tier, sync.Namespace) != CachingTier.Full || sync.PausesWhileUsed && isUsed) return false;
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

        // On the full tier a catalog read whole once answers Discover itself; only its full sync reads a catalog whole, and that runs only for a catalog with a full list.
        public static async Task<IReadOnlySet<string>> ReadWholeExternalWorkCatalogsAsync(this LogicContext ctx, CancellationToken ct)
        {
            if (await ctx.ReadCachingTierAsync(ct) != CachingTier.Full) return new HashSet<string>(StringComparer.Ordinal);

            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    sync."Namespace"
                FROM
                    "ExternalWorkSyncs" AS sync
                WHERE
                    sync."CompletedAt" IS NOT NULL;
                """;
            return (await ctx.ReadSql.ReadListAsync<ExternalWorkNamespaceData>(sql, ct)).Select(row => row.Namespace).ToHashSet(StringComparer.Ordinal);
        }

        // Every shelf in one statement, each in its own order; the Shelf number keeps them apart.
        public static async Task<IReadOnlyList<ExternalWorkShelfItemData>> ReadExternalWorkShelvesAsync(this LogicContext ctx, ExternalWorkShelfQueryData data, CancellationToken ct)
        {
            // Feed numbers as DiscoverFeedType: 1 trending, 2 top, 3 new, 4 upcoming; only the shelf's own list is read, in the order of its index.
            var sql = new SqlStatement { DisableJit = true };
            sql.Statement =
                """
                SELECT
                    shelf."Shelf",
                    work."CatalogNamespace" AS "Namespace",
                    identity."Value",
                    work."CanonicalTitle" AS "Title",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    work."Overview",
                    work."Creator",
                    coalesce(known."StoredImageUrl", work."ImageUrl") AS "ImageUrl",
                    work."BackdropUrl",
                    work."Rating",
                    work."Id" AS "WorkId",
                    known."MediaStateTypeId",
                    known."PlexUrl"
                FROM
                    jsonb_to_recordset(@Shelves::jsonb) AS shelf ("Shelf" integer, "Namespace" text, "MediaTypeId" smallint, "MediaSubtypeId" smallint, "Feed" smallint)
                    CROSS JOIN LATERAL unnest(CASE shelf."Feed"
                        WHEN 1 THEN ARRAY(
                            SELECT work."Id" FROM "Works" AS work
                            WHERE work."CatalogNamespace" = shelf."Namespace" AND work."MediaTypeId" = shelf."MediaTypeId" AND coalesce(work."MediaSubtypeId", 0) = shelf."MediaSubtypeId"
                                AND NOT work."IsAdult" AND work."ImageUrl" IS NOT NULL AND work."TrendingRank" IS NOT NULL
                            ORDER BY work."TrendingRank", work."Id"
                            LIMIT @Limit OFFSET @Offset)
                        WHEN 2 THEN ARRAY(
                            SELECT work."Id" FROM "Works" AS work
                            WHERE work."CatalogNamespace" = shelf."Namespace" AND work."MediaTypeId" = shelf."MediaTypeId" AND coalesce(work."MediaSubtypeId", 0) = shelf."MediaSubtypeId"
                                AND NOT work."IsAdult" AND work."ImageUrl" IS NOT NULL AND work."Votes" >= @TopVotes
                            ORDER BY work."Rating" DESC NULLS LAST, work."Votes" DESC, work."Id"
                            LIMIT @Limit OFFSET @Offset)
                        WHEN 3 THEN ARRAY(
                            SELECT work."Id" FROM "Works" AS work
                            WHERE work."CatalogNamespace" = shelf."Namespace" AND work."MediaTypeId" = shelf."MediaTypeId" AND coalesce(work."MediaSubtypeId", 0) = shelf."MediaSubtypeId"
                                AND NOT work."IsAdult" AND work."ImageUrl" IS NOT NULL AND work."ReleasedOn" BETWEEN @NewSince AND @Today
                            ORDER BY work."Popularity" DESC NULLS LAST, work."Id"
                            LIMIT @Limit OFFSET @Offset)
                        WHEN 4 THEN ARRAY(
                            SELECT work."Id" FROM "Works" AS work
                            WHERE work."CatalogNamespace" = shelf."Namespace" AND work."MediaTypeId" = shelf."MediaTypeId" AND coalesce(work."MediaSubtypeId", 0) = shelf."MediaSubtypeId"
                                AND NOT work."IsAdult" AND work."ImageUrl" IS NOT NULL AND work."ReleasedOn" > @Today AND work."ReleasedOn" <= @UpcomingUntil
                            ORDER BY work."Popularity" DESC NULLS LAST, work."Id"
                            LIMIT @Limit OFFSET @Offset)
                    END) WITH ORDINALITY AS picked ("Id", "Place")
                    JOIN "Works" AS work ON work."Id" = picked."Id"
                    JOIN "WorkExternalIdentities" AS identity ON identity."WorkId" = work."Id" AND identity."Namespace" = work."CatalogNamespace"
                    LEFT JOIN LATERAL (
                        -- The profile's view of the Work, only when the profile may see it.
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
                            (
                                SELECT
                                    scoped."WorkId"
                                FROM
                                    "ProfileWorks" AS scoped
                                WHERE
                                    scoped."ProfileId" = @ProfileId
                                    AND scoped."WorkId" = work."Id"
                                OFFSET 0
                            ) AS visible
                            LEFT JOIN LATERAL (
                                SELECT
                                    scoped."MediaStateTypeId"
                                FROM
                                    "ProfileWorkStates" AS scoped
                                WHERE
                                    scoped."ProfileId" = @ProfileId
                                    AND scoped."WorkId" = visible."WorkId"
                                OFFSET 0
                            ) AS state ON TRUE
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
                        work."Id",
                        row_number() OVER (
                            PARTITION BY work."CatalogNamespace"
                            ORDER BY work."TrendingRank" IS NULL, lower(work."CanonicalTitle") LIKE @TitlePrefix DESC, work."Popularity" DESC NULLS LAST, work."Id"
                        ) AS "Place"
                    FROM
                        "Works" AS work
                        JOIN jsonb_to_recordset(@Shelves::jsonb) AS scope ("Shelf" integer, "Namespace" text, "MediaTypeId" smallint, "MediaSubtypeId" smallint)
                            ON scope."Namespace" = work."CatalogNamespace" AND scope."MediaTypeId" = work."MediaTypeId" AND scope."MediaSubtypeId" = coalesce(work."MediaSubtypeId", 0)
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
                        -- A title two catalogs know as one film (its IMDb id) shows once.
                        SELECT DISTINCT ON (coalesce(imdb."Value", work."Id"::text))
                            candidate."Shelf",
                            candidate."Place",
                            known."IsInLibrary",
                            work."CatalogNamespace" AS "Namespace",
                            identity."Value",
                            work."CanonicalTitle" AS "Title",
                            work."ReleaseYear"::integer AS "ReleaseYear",
                            work."Overview",
                            work."Creator",
                            coalesce(known."StoredImageUrl", work."ImageUrl") AS "ImageUrl",
                            work."BackdropUrl",
                            work."Rating",
                            work."Id" AS "WorkId",
                            known."MediaStateTypeId",
                            known."PlexUrl"
                        FROM
                            candidate
                            JOIN "Works" AS work ON work."Id" = candidate."Id"
                            JOIN "WorkExternalIdentities" AS identity ON identity."WorkId" = work."Id" AND identity."Namespace" = work."CatalogNamespace"
                            LEFT JOIN "WorkExternalIdentities" AS imdb ON imdb."WorkId" = work."Id" AND imdb."Namespace" = 'imdb'
                            LEFT JOIN LATERAL (
                                SELECT
                                    visible."WorkId",
                                    state."MediaStateTypeId",
                                    EXISTS (SELECT 1 FROM "WorkLibraryRelations" AS library WHERE library."WorkId" = visible."WorkId") AS "IsInLibrary",
                                    (
                                        SELECT
                                            fact."Value"
                                        FROM
                                            "WorkMetadataFacts" AS fact
                                        WHERE
                                            fact."WorkId" = visible."WorkId"
                                            AND fact."FactKey" = 'Poster'
                                            AND fact."Value" LIKE '/api/v1/covers/%'
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
                                    AND visible."WorkId" = work."Id"
                            ) AS known ON TRUE
                        WHERE
                            candidate."Place" <= @Limit
                        ORDER BY
                            coalesce(imdb."Value", work."Id"::text),
                            known."IsInLibrary" IS NOT TRUE,
                            candidate."Place"
                    ) AS found
                ORDER BY
                    found."IsInLibrary" IS NOT TRUE,
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
                    sync."RequestsPerMinute"::integer AS "RequestsPerMinute",
                    sync."PausesWhileUsed",
                    sync."ListedOn",
                    (SELECT count(*) FROM "Works" AS work WHERE work."CatalogNamespace" = sync."Namespace") AS "Entries",
                    (SELECT count(*) FROM "Works" AS work WHERE work."CatalogNamespace" = sync."Namespace" AND work."FetchedAt" IS NULL) AS "Pending",
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
            var changed = await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            // Build planner statistics once, before this transaction exposes a newly completed catalog.
            if (changed > 0) await ctx.ExecuteAsyncForAffectedRows(new SqlStatement { Statement = "ANALYZE \"Works\"" }, ct);
            return changed;
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

        // A provider's id list adds the titles not yet known as minimal Works; known ones only take its popularity, their titles stay.
        // A Work another catalog keeps is left as it is.
        public static async Task<int> ListExternalWorksAsync(this LogicContext ctx, ExternalWorkEntriesData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement = "SELECT pg_advisory_xact_lock(hashtextextended('catalog-works:' || @Namespace, 0));";
            sql.Parameters.Add(data);
            await ctx.Sql.ExecuteScalarAsync(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                WITH entry AS (
                    SELECT DISTINCT ON (entry."Value")
                        entry."Value",
                        left(entry."Title", 500) AS "Title",
                        greatest(entry."Popularity", 0) AS "Popularity"
                    FROM
                        jsonb_to_recordset(@Entries::jsonb) AS entry ("Value" text, "Title" text, "Popularity" real)
                    WHERE
                        length(btrim(entry."Title")) > 0
                    ORDER BY
                        entry."Value"
                ), known AS (
                    SELECT
                        entry."Value",
                        entry."Popularity",
                        identity."WorkId"
                    FROM
                        entry
                        JOIN "WorkExternalIdentities" AS identity ON identity."Namespace" = @Namespace AND identity."Value" = entry."Value"
                ), updated AS (
                    UPDATE
                        "Works" AS work
                    SET
                        "Popularity" = known."Popularity",
                        "CatalogNamespace" = @Namespace
                    FROM
                        known
                    WHERE
                        work."Id" = known."WorkId"
                        AND (work."CatalogNamespace" IS NULL OR work."CatalogNamespace" = @Namespace)
                        AND (work."CatalogNamespace" IS NULL OR work."Popularity" IS DISTINCT FROM known."Popularity")
                    RETURNING
                        work."Id"
                ), fresh AS (
                    SELECT
                        entry."Value",
                        entry."Title",
                        entry."Popularity",
                        nextval(pg_get_serial_sequence('"Works"', 'Id')) AS "WorkId"
                    FROM
                        entry
                    WHERE
                        NOT EXISTS (SELECT 1 FROM known WHERE known."Value" = entry."Value")
                ), created AS (
                    INSERT INTO
                        "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "OriginalTitle", "Popularity", "CatalogNamespace")
                    SELECT
                        fresh."WorkId",
                        @MediaTypeId,
                        @MediaSubtypeId::smallint,
                        fresh."Title",
                        fresh."Title",
                        fresh."Popularity",
                        @Namespace
                    FROM
                        fresh
                    RETURNING
                        "Id"
                ), identified AS (
                    INSERT INTO
                        "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                    SELECT
                        fresh."WorkId",
                        @Namespace,
                        fresh."Value"
                    FROM
                        fresh
                )
                SELECT
                    ((SELECT count(*) FROM updated) + (SELECT count(*) FROM created))::integer;
                """;
            sql.Parameters.Add(data);
            return (int)(await ctx.Sql.ExecuteScalarAsync(sql, ct))!;
        }

        // The catalog entries the full sync has not read yet, most popular first.
        public static async Task<IReadOnlyList<string>> ReadUndetailedExternalWorksAsync(this LogicContext ctx, ExternalWorkQueueData queue, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    identity."Value"
                FROM
                    "Works" AS work
                    JOIN "WorkExternalIdentities" AS identity ON identity."WorkId" = work."Id" AND identity."Namespace" = work."CatalogNamespace"
                WHERE
                    work."CatalogNamespace" = @Namespace
                    AND work."FetchedAt" IS NULL
                ORDER BY
                    work."Popularity" DESC NULLS LAST,
                    work."Id"
                LIMIT @Limit;
                """;
            sql.Parameters.Add(queue);
            return [.. (await ctx.ReadSql.ReadListAsync<ExternalWorkValueData>(sql, ct)).Select(row => row.Value)];
        }

        // Entries just read replace what the catalog knew. An entry the provider no longer has takes its Work along while that Work is only a catalog title;
        // a Work with details, a tie to the library or a relation pointing at it stays and is not read again.
        public static async Task<int> DetailExternalWorksAsync(this LogicContext ctx, ExternalWorkEntriesData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "Works" AS work
                USING
                    "WorkExternalIdentities" AS identity
                WHERE
                    identity."Namespace" = @Namespace
                    AND identity."Value" = ANY(@Gone)
                    AND work."Id" = identity."WorkId"
                    AND work."CatalogNamespace" = @Namespace
                    AND work."DetailedAt" IS NULL
                    AND NOT EXISTS (SELECT 1 FROM "WorkLibraryRelations" AS library WHERE library."WorkId" = work."Id")
                    AND NOT EXISTS (SELECT 1 FROM "WorkRelations" AS relation WHERE relation."RelatedWorkId" = work."Id");
                """;
            sql.Parameters.Add(data);
            var changed = await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Works" AS work
                SET
                    "FetchedAt" = now()
                FROM
                    "WorkExternalIdentities" AS identity
                WHERE
                    identity."Namespace" = @Namespace
                    AND identity."Value" = ANY(@Gone)
                    AND work."Id" = identity."WorkId"
                    AND work."CatalogNamespace" = @Namespace;
                """;
            sql.Parameters.Add(data);
            changed += await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // Title, year and date belong to the details once a Work has them; the catalog fields are the catalog's.
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Works" AS work
                SET
                    "CatalogNamespace" = @Namespace,
                    "CanonicalTitle" = CASE WHEN work."DetailedAt" IS NULL THEN left(entry."Title", 500) ELSE work."CanonicalTitle" END,
                    "ReleasedOn" = CASE WHEN work."DetailedAt" IS NULL THEN entry."ReleasedOn" ELSE work."ReleasedOn" END,
                    "ReleaseYear" = CASE
                        WHEN work."DetailedAt" IS NOT NULL THEN work."ReleaseYear"
                        WHEN extract(year FROM entry."ReleasedOn") BETWEEN 1800 AND 2200 THEN extract(year FROM entry."ReleasedOn")::smallint
                    END,
                    "OriginalTitle" = left(entry."OriginalTitle", 500),
                    "Overview" = left(entry."Overview", 4000),
                    "ImageUrl" = CASE WHEN entry."ImageUrl" ~ '^https://' AND length(entry."ImageUrl") <= 500 THEN entry."ImageUrl" END,
                    "BackdropUrl" = CASE WHEN entry."BackdropUrl" ~ '^https://' AND length(entry."BackdropUrl") <= 500 THEN entry."BackdropUrl" END,
                    "Popularity" = coalesce(greatest(entry."Popularity", 0), work."Popularity"),
                    "Rating" = CASE WHEN entry."Rating" BETWEEN 0 AND 10 THEN round(entry."Rating"::numeric, 2) END,
                    "Votes" = CASE WHEN entry."Votes" >= 0 THEN entry."Votes" END,
                    "IsAdult" = entry."IsAdult",
                    "FetchedAt" = now()
                FROM
                    jsonb_to_recordset(@Entries::jsonb) AS entry ("Value" text, "Title" text, "OriginalTitle" text, "ReleasedOn" date, "Overview" text, "ImageUrl" text,
                        "BackdropUrl" text, "Popularity" real, "Rating" double precision, "Votes" integer, "ImdbId" text, "IsAdult" boolean)
                    JOIN "WorkExternalIdentities" AS identity ON identity."Namespace" = @Namespace AND identity."Value" = entry."Value"
                WHERE
                    work."Id" = identity."WorkId"
                    AND (work."CatalogNamespace" IS NULL OR work."CatalogNamespace" = @Namespace)
                    AND length(btrim(entry."Title")) > 0;
                """;
            sql.Parameters.Add(data);
            changed += await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // A Work without details takes the entry's IMDb id while no other Work holds it.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                SELECT
                    work."Id",
                    'imdb',
                    entry."ImdbId"
                FROM
                    jsonb_to_recordset(@Entries::jsonb) AS entry ("Value" text, "ImdbId" text)
                    JOIN "WorkExternalIdentities" AS identity ON identity."Namespace" = @Namespace AND identity."Value" = entry."Value"
                    JOIN "Works" AS work ON work."Id" = identity."WorkId" AND work."CatalogNamespace" = @Namespace AND work."DetailedAt" IS NULL
                WHERE
                    entry."ImdbId" ~ '^tt[0-9]{7,10}$'
                ON CONFLICT DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            return changed;
        }

        // Entries the provider changed are read again.
        public static async Task<int> MarkChangedExternalWorksAsync(this LogicContext ctx, ExternalWorkValuesData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Works" AS work
                SET
                    "FetchedAt" = NULL
                FROM
                    "WorkExternalIdentities" AS identity
                WHERE
                    identity."Namespace" = @Namespace
                    AND identity."Value" = ANY(@Values)
                    AND work."Id" = identity."WorkId"
                    AND work."CatalogNamespace" = @Namespace
                    AND work."FetchedAt" IS NOT NULL;
                """;
            sql.Parameters.Add(data);
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // Opening a Work the profile may see counts once a day; null when the profile may not open it.
        public static Task<WorkDetailStateData?> MarkWorkViewedAsync(this LogicContext ctx, long workId, long profileId, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH visible AS (
                    SELECT
                        work."Id",
                        work."MediaTypeId",
                        work."MediaSubtypeId",
                        work."DetailedAt" IS NOT NULL AS "IsDetailed"
                    FROM
                        "ProfileWorks" AS visible
                        JOIN "Works" AS work ON work."Id" = visible."WorkId"
                    WHERE
                        visible."ProfileId" = @ProfileId
                        AND work."Id" = @Id
                ), viewed AS (
                    UPDATE
                        "Works" AS work
                    SET
                        "ViewedAt" = now()
                    FROM
                        visible
                    WHERE
                        work."Id" = visible."Id"
                        AND (work."ViewedAt" < now() - interval '1 day' OR work."ViewedAt" IS NULL)
                )
                SELECT
                    visible."Id",
                    visible."MediaTypeId",
                    visible."MediaSubtypeId",
                    visible."IsDetailed",
                    NULL::text AS "AniListValue"
                FROM
                    visible;
                """;
            sql.Parameters.Add(new WorkIdData(workId));
            sql.Parameters.Add(new ProfileParameters(profileId));
            return ctx.ReadOptionalAsync<WorkDetailStateData>(sql, ct);
        }
    }
}
