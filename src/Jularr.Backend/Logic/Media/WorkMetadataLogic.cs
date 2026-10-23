using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class WorkMetadataLogic
    {
        // A Work matched to the wrong provider entry takes the chosen one: its provider identities, titles, facts and units without files go,
        // its files, requests and manual edits stay; the following metadata import fills it again.
        public static async Task RematchWorkAsync(this LogicContext ctx, WorkRematchData data, MediaType mediaType, CancellationToken ct)
        {
            var allowed = mediaType switch
            {
                MediaType.Movie => data.Namespace == "tmdb_movie",
                MediaType.Series => data.Namespace == "tmdb_tv",
                MediaType.Book => data.Namespace is "openlibrary" or "anilist",
                MediaType.Music => data.Namespace == "musicbrainz",
                MediaType.Game => data.Namespace == "igdb",
                _ => false
            };
            if (!allowed)
            {
                throw new DataValidationException(nameof(data.Namespace), "unknown_catalog_value");
            }

            // Another Work with that entry is a different title, not a correction.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    identity."WorkId" AS "Id"
                FROM
                    "WorkExternalIdentities" AS identity
                WHERE
                    identity."Namespace" = @Namespace
                    AND identity."Value" = @Value
                    AND identity."WorkId" <> @Id;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<WorkIdData>(sql, ct) is not null)
            {
                throw new DataValidationException("ExternalId", "in_use");
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Works" AS work
                SET
                    "CanonicalTitle" = @CanonicalTitle,
                    "ReleaseYear" = @ReleaseYear,
                    "ReleasedOn" = NULL,
                    "Version" = work."Version" + 1,
                    "UpdatedAt" = now()
                WHERE
                    work."Id" = @Id
                    AND work."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "outdated_version");
            }

            // Provider facts of the old match go; links to media servers and manual entries stay.
            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "WorkExternalIdentities" AS identity
                WHERE
                    identity."WorkId" = @Id
                    AND NOT identity."IsManual"
                    AND identity."Namespace" IN ('tmdb_movie', 'tmdb_tv', 'tvdb', 'imdb', 'openlibrary', 'isbn', 'anilist', 'musicbrainz', 'igdb');

                DELETE FROM
                    "WorkTitles" AS title
                WHERE
                    title."WorkId" = @Id
                    AND NOT title."IsManual";

                DELETE FROM
                    "WorkMetadataFacts" AS fact
                WHERE
                    fact."WorkId" = @Id
                    AND NOT fact."IsManual";

                DELETE FROM
                    "WorkUnits" AS unit
                WHERE
                    unit."WorkId" = @Id
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "MediaAssets" AS asset
                            LEFT JOIN "WorkUnits" AS child ON child."Id" = asset."WorkUnitId"
                        WHERE
                            asset."WorkUnitId" = unit."Id"
                            OR child."ParentWorkUnitId" = unit."Id"
                    );

                INSERT INTO
                    "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                VALUES
                    (@Id, @Namespace, @Value);
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task SaveWorkRuntimeAsync(this LogicContext ctx, WorkRuntimeData data, CancellationToken ct)
        {
            // A provider that knows no length keeps the stored one.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Works"
                SET
                    "RuntimeMinutes" = COALESCE(@RuntimeMinutes, "RuntimeMinutes")
                WHERE
                    "Id" = @Id;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task<long> SaveWorkMetadataAsync(this LogicContext ctx, WorkMetadataData data, CancellationToken ct)
        {
            // Video provider IDs must agree on one canonical Work before any metadata is written.
            var sql = new SqlStatement();
            long? workId;
            if (data.MediaTypeId is MediaType.Movie or MediaType.Series)
            {
                sql.Statement =
                    """
                    WITH requested AS (
                        SELECT @Namespace::text AS "Namespace", @Value::text AS "Value"
                        UNION
                        SELECT item."Namespace", item."Value"
                        FROM jsonb_to_recordset(@ExternalIdentities::jsonb) AS item ("Namespace" text, "Value" text)
                    )
                    SELECT
                        work."Id",
                        work."MediaTypeId"
                    FROM
                        requested
                        JOIN "WorkExternalIdentities" AS identity ON identity."Namespace" = requested."Namespace" AND identity."Value" = requested."Value"
                        JOIN "Works" AS work ON work."Id" = identity."WorkId"
                    FOR UPDATE OF work;
                    """;
                sql.Parameters.Add(data);
                var matches = (await ctx.Sql.ReadListAsync<WorkIdentityCandidateData>(sql, ct)).DistinctBy(candidate => candidate.Id).ToArray();
                if (matches.Length > 1 || matches.Any(candidate => candidate.MediaTypeId != data.MediaTypeId))
                {
                    throw new DataValidationException(nameof(data.ExternalIdentities), "in_use");
                }

                workId = matches.SingleOrDefault()?.Id;
                if (workId is { } known)
                {
                    // An unchanged shared IMDb ID cannot silently replace a different stored TMDB or TVDB ID.
                    sql = new SqlStatement();
                    sql.Statement =
                        """
                        WITH requested AS (
                            SELECT @Namespace::text AS "Namespace", @Value::text AS "Value"
                            UNION
                            SELECT item."Namespace", item."Value"
                            FROM jsonb_to_recordset(@ExternalIdentities::jsonb) AS item ("Namespace" text, "Value" text)
                        )
                        SELECT EXISTS (
                            SELECT 1
                            FROM requested
                            JOIN "WorkExternalIdentities" AS identity ON identity."Namespace" = requested."Namespace"
                            WHERE identity."WorkId" = @Id AND identity."Value" <> requested."Value"
                        );
                        """;
                    sql.Parameters.Add(data);
                    sql.Parameters.Add(new WorkIdData(known));
                    if ((bool)(await ctx.ReadSql.ExecuteScalarAsync(sql, ct))!)
                    {
                        throw new DataValidationException(nameof(data.ExternalIdentities), "in_use");
                    }
                }
            }
            else
            {
                // Other catalog types retain their existing provider-specific matching.
                sql.Statement =
                    """
                    SELECT
                        work."Id"
                    FROM
                        "WorkExternalIdentities" AS identity
                        JOIN "Works" AS work ON work."Id" = identity."WorkId"
                    WHERE
                        identity."Namespace" = @Namespace
                        AND identity."Value" = @Value
                        AND work."MediaTypeId" = @MediaTypeId
                    FOR UPDATE OF work;
                    """;
                sql.Parameters.Add(data);
                workId = (await ctx.ReadOptionalAsync<WorkIdData>(sql, ct))?.Id;
            }

            // A Book of the same subtype, title and year from another provider is the same Work; anything less clear becomes a new one.
            if (workId is null && data.MediaTypeId == MediaType.Book)
            {
                sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        work."Id"
                    FROM
                        "Works" AS work
                    WHERE
                        work."MediaTypeId" = 3
                        AND work."MediaSubtypeId" IS NOT DISTINCT FROM @MediaSubtypeId
                        AND (@ReleaseYear::integer IS NULL OR work."ReleaseYear" IS NULL OR abs(work."ReleaseYear" - @ReleaseYear) <= 1)
                        AND NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "WorkExternalIdentities" AS identity
                            WHERE
                                identity."WorkId" = work."Id"
                                AND identity."Namespace" = @Namespace
                        )
                        AND EXISTS (
                            SELECT
                                1
                            FROM
                                "WorkTitles" AS title
                            WHERE
                                title."WorkId" = work."Id"
                                AND btrim(regexp_replace(lower(title."Title"), '[^[:alnum:]]+', ' ', 'g'))
                                    IN (btrim(regexp_replace(lower(@CanonicalTitle), '[^[:alnum:]]+', ' ', 'g')), btrim(regexp_replace(lower(@OriginalTitle), '[^[:alnum:]]+', ' ', 'g')))
                        )
                    LIMIT 2
                    FOR UPDATE OF work;
                    """;
                sql.Parameters.Add(data);
                var same = await ctx.Sql.ReadListAsync<WorkIdData>(sql, ct);
                workId = same.Count == 1 ? same[0].Id : null;
            }

            sql = new SqlStatement();
            sql.Statement = workId is null
                ? """
                  INSERT INTO
                      "Works" ("MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear", "ReleasedOn", "OriginalLanguage")
                  VALUES
                      (@MediaTypeId, @MediaSubtypeId, @CanonicalTitle, @ReleaseYear, @ReleasedOn, @OriginalLanguage)
                  RETURNING
                      "Id";
                  """
                : """
                  UPDATE
                      "Works" AS work
                  SET
                      "ReleaseYear" = COALESCE(work."ReleaseYear", @ReleaseYear),
                      "ReleasedOn" = COALESCE(@ReleasedOn, work."ReleasedOn"),
                      "OriginalLanguage" = COALESCE(@OriginalLanguage, work."OriginalLanguage"),
                      "MediaSubtypeId" = CASE WHEN work."IsMediaSubtypeManual" THEN work."MediaSubtypeId" ELSE COALESCE(@MediaSubtypeId, work."MediaSubtypeId") END,
                      "UpdatedAt" = now()
                  WHERE
                      work."Id" = @Id
                  RETURNING
                      work."Id";
                  """;
            sql.Parameters.Add(data);
            if (workId is { } existing)
            {
                sql.Parameters.Add(new WorkIdData(existing));
            }

            workId = (await ctx.ReadRequiredAsync<WorkIdData>(sql, ct)).Id;

            // Provider titles are replaced; manual titles are never touched.
            sql = new SqlStatement();
            sql.Statement =
                """
                WITH provider AS (
                    SELECT
                        "Id"
                    FROM
                        "Providers"
                    WHERE
                        "Key" = @ProviderKey
                ), removed AS (
                    DELETE FROM
                        "WorkTitles" AS title
                    USING
                        provider
                    WHERE
                        title."WorkId" = @Id
                        AND title."ProviderId" = provider."Id"
                        AND NOT title."IsManual"
                )
                INSERT INTO
                    "WorkTitles" ("WorkId", "Title", "ProviderId", "IsManual")
                SELECT DISTINCT
                    @Id,
                    candidate."Title",
                    provider."Id",
                    FALSE
                FROM
                    provider
                    CROSS JOIN (VALUES (@CanonicalTitle), (@OriginalTitle)) AS candidate ("Title")
                WHERE
                    candidate."Title" IS NOT NULL;
                """;
            sql.Parameters.Add(data);
            sql.Parameters.Add(new WorkIdData(workId.Value));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // Each enabled language takes its translated title: the one of its region, else of its language.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkTitles" ("WorkId", "UiLocaleId", "Title", "ProviderId", "IsManual")
                SELECT DISTINCT ON (locale."Id")
                    @Id,
                    locale."Id",
                    btrim(item."Title"),
                    provider."Id",
                    FALSE
                FROM
                    jsonb_to_recordset(@Translations::jsonb) AS item ("Language" text, "Region" text, "Title" text, "Overview" text)
                    JOIN "UiLocales" AS locale ON locale."IsEnabled" AND lower(split_part(locale."Locale", '-', 1)) = lower(item."Language")
                    CROSS JOIN "Providers" AS provider
                WHERE
                    provider."Key" = @ProviderKey
                    AND length(btrim(item."Title")) BETWEEN 1 AND 500
                ORDER BY
                    locale."Id",
                    lower(locale."Locale") = lower(item."Language" || '-' || COALESCE(item."Region", '')) DESC;
                """;
            sql.Parameters.Add(data);
            sql.Parameters.Add(new WorkIdData(workId.Value));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // Provider facts are replaced; manual facts stay, a fact another provider gives stays with it.
            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "WorkMetadataFacts" AS fact
                USING
                    "Providers" AS provider
                WHERE
                    fact."WorkId" = @Id
                    AND fact."ProviderId" = provider."Id"
                    AND provider."Key" = @ProviderKey
                    AND NOT fact."IsManual";
                """;
            sql.Parameters.Add(data);
            sql.Parameters.Add(new WorkIdData(workId.Value));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkMetadataFacts" ("WorkId", "FactKey", "UiLocaleId", "Value", "ProviderId")
                SELECT
                    @Id,
                    item."Key",
                    CASE WHEN item."Localized" THEN settings."UiLocaleId" END,
                    -- An image already kept locally from this very address stays the local one.
                    COALESCE((
                        SELECT
                            cover."LocalUrl"
                        FROM
                            "WorkCovers" AS cover
                        WHERE
                            cover."WorkId" = @Id
                            AND cover."WorkUnitId" IS NULL
                            AND cover."CoverKindId" = CASE item."Key" WHEN 'Poster' THEN 1 WHEN 'Backdrop' THEN 2 END
                            AND cover."SourceUrl" = item."Value"
                            AND cover."LocalUrl" IS NOT NULL
                    ), item."Value"),
                    provider."Id"
                FROM
                    jsonb_to_recordset(@Facts::jsonb) AS item ("Key" text, "Value" text, "Localized" boolean)
                    CROSS JOIN "Providers" AS provider
                    CROSS JOIN "InstanceSettings" AS settings
                WHERE
                    provider."Key" = @ProviderKey
                    AND length(btrim(item."Value")) > 0
                ON CONFLICT DO NOTHING;
                """;
            sql.Parameters.Add(data);
            sql.Parameters.Add(new WorkIdData(workId.Value));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // And its translated summary; the instance language keeps the provider's own.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkMetadataFacts" ("WorkId", "FactKey", "UiLocaleId", "Value", "ProviderId")
                SELECT DISTINCT ON (locale."Id")
                    @Id,
                    'Overview',
                    locale."Id",
                    left(btrim(item."Overview"), 4000),
                    provider."Id"
                FROM
                    jsonb_to_recordset(@Translations::jsonb) AS item ("Language" text, "Region" text, "Title" text, "Overview" text)
                    JOIN "UiLocales" AS locale ON locale."IsEnabled" AND lower(split_part(locale."Locale", '-', 1)) = lower(item."Language")
                    CROSS JOIN "Providers" AS provider
                WHERE
                    provider."Key" = @ProviderKey
                    AND length(btrim(item."Overview")) > 0
                ORDER BY
                    locale."Id",
                    lower(locale."Locale") = lower(item."Language" || '-' || COALESCE(item."Region", '')) DESC
                ON CONFLICT DO NOTHING;
                """;
            sql.Parameters.Add(data);
            sql.Parameters.Add(new WorkIdData(workId.Value));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // The provider's own id and its mappings refresh unless corrected by hand; a mapping owned by another Work is left alone.
            sql = new SqlStatement();
            sql.Statement =
                """
                WITH item AS (
                    SELECT
                        "Namespace",
                        "Value"
                    FROM
                        jsonb_to_recordset(@ExternalIdentities::jsonb) AS item ("Namespace" text, "Value" text)
                    UNION
                    SELECT
                        @Namespace,
                        @Value
                ), updated AS (
                    UPDATE
                        "WorkExternalIdentities" AS identity
                    SET
                        "Value" = item."Value",
                        "UpdatedAt" = now()
                    FROM
                        item
                    WHERE
                        identity."WorkId" = @Id
                        AND identity."Namespace" = item."Namespace"
                        AND NOT identity."IsManual"
                        AND NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "WorkExternalIdentities" AS other
                            WHERE
                                other."Namespace" = item."Namespace"
                                AND other."Value" = item."Value"
                                AND other."WorkId" <> @Id
                        )
                    RETURNING
                        identity."Namespace"
                )
                INSERT INTO
                    "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                SELECT
                    @Id,
                    item."Namespace",
                    item."Value"
                FROM
                    item
                WHERE
                    item."Namespace" NOT IN (SELECT "Namespace" FROM updated)
                ON CONFLICT DO NOTHING;
                """;
            sql.Parameters.Add(data);
            sql.Parameters.Add(new WorkIdData(workId.Value));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // Seasons first, then their episodes, or volumes and tracks; existing units keep their identity.
            sql = new SqlStatement();
            sql.Statement =
                """
                WITH item AS (
                    SELECT
                        *
                    FROM
                        jsonb_to_recordset(@Units::jsonb) AS item ("Season" integer, "Episode" integer, "Volume" integer, "Track" integer, "Title" text, "ReleasedOn" date,
                            "Overview" text, "ImageUrl" text, "RuntimeMinutes" integer, "Disc" integer)
                ), monitoring AS (
                    SELECT
                        COALESCE(bool_and(wanted."MonitorsNewUnits"), TRUE) AS "MonitorsNewUnits"
                    FROM
                        "WantedItems" AS wanted
                    WHERE
                        wanted."WorkId" = @Id
                ), leaves AS (
                    INSERT INTO
                        "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "Title", "ReleasedOn", "IsMonitored", "DiscNumber")
                    SELECT
                        @Id,
                        NULL,
                        CASE WHEN item."Volume" IS NOT NULL THEN 3 ELSE 5 END,
                        COALESCE(item."Volume", item."Track"),
                        NULLIF(btrim(item."Title"), ''),
                        item."ReleasedOn",
                        monitoring."MonitorsNewUnits",
                        item."Disc"
                    FROM
                        item
                        CROSS JOIN monitoring
                    WHERE
                        item."Volume" IS NOT NULL
                        OR item."Track" IS NOT NULL
                    ON CONFLICT ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") DO UPDATE
                    SET
                        "Title" = COALESCE(EXCLUDED."Title", "WorkUnits"."Title"),
                        "ReleasedOn" = COALESCE(EXCLUDED."ReleasedOn", "WorkUnits"."ReleasedOn"),
                        "DiscNumber" = COALESCE(EXCLUDED."DiscNumber", "WorkUnits"."DiscNumber")
                ), seasons AS (
                    INSERT INTO
                        "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "Title", "ReleasedOn", "ImageUrl")
                    SELECT
                        @Id,
                        NULL,
                        1,
                        item."Season",
                        NULLIF(btrim(item."Title"), ''),
                        item."ReleasedOn",
                        item."ImageUrl"
                    FROM
                        item
                    WHERE
                        item."Season" IS NOT NULL
                        AND item."Episode" IS NULL
                    ON CONFLICT ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") DO UPDATE
                    SET
                        "Title" = COALESCE(EXCLUDED."Title", "WorkUnits"."Title"),
                        "ReleasedOn" = EXCLUDED."ReleasedOn",
                        "ImageUrl" = COALESCE((
                        SELECT
                            cover."LocalUrl"
                        FROM
                            "WorkCovers" AS cover
                        WHERE
                            cover."WorkUnitId" = "WorkUnits"."Id"
                            AND cover."SourceUrl" = EXCLUDED."ImageUrl"
                            AND cover."LocalUrl" IS NOT NULL
                    ), EXCLUDED."ImageUrl", "WorkUnits"."ImageUrl")
                    RETURNING
                        "Id",
                        "Number"
                )
                INSERT INTO
                    "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "Title", "ReleasedOn", "IsMonitored", "Overview", "ImageUrl", "RuntimeMinutes")
                SELECT
                    @Id,
                    seasons."Id",
                    2,
                    item."Episode",
                    NULLIF(btrim(item."Title"), ''),
                    item."ReleasedOn",
                    item."Season" > 0 AND monitoring."MonitorsNewUnits",
                    NULLIF(btrim(item."Overview"), ''),
                    item."ImageUrl",
                    item."RuntimeMinutes"
                FROM
                    item
                    JOIN seasons ON seasons."Number" = item."Season"
                    CROSS JOIN monitoring
                WHERE
                    item."Episode" IS NOT NULL
                ON CONFLICT ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") DO UPDATE
                SET
                    "Title" = COALESCE(EXCLUDED."Title", "WorkUnits"."Title"),
                    "ReleasedOn" = EXCLUDED."ReleasedOn",
                    "Overview" = COALESCE(EXCLUDED."Overview", "WorkUnits"."Overview"),
                    "ImageUrl" = COALESCE((
                        SELECT
                            cover."LocalUrl"
                        FROM
                            "WorkCovers" AS cover
                        WHERE
                            cover."WorkUnitId" = "WorkUnits"."Id"
                            AND cover."SourceUrl" = EXCLUDED."ImageUrl"
                            AND cover."LocalUrl" IS NOT NULL
                    ), EXCLUDED."ImageUrl", "WorkUnits"."ImageUrl"),
                    "RuntimeMinutes" = COALESCE(EXCLUDED."RuntimeMinutes", "WorkUnits"."RuntimeMinutes");
                """;
            sql.Parameters.Add(data);
            sql.Parameters.Add(new WorkIdData(workId.Value));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // An album's tracks follow its edition; tracks holding files stay.
            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "WorkUnits" AS track
                WHERE
                    track."WorkId" = @Id
                    AND track."WorkUnitTypeId" = 5
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            jsonb_to_recordset(@Units::jsonb) AS item ("Track" integer)
                        WHERE
                            item."Track" IS NOT NULL
                    )
                    AND track."Number" > (
                        SELECT
                            max(item."Track")
                        FROM
                            jsonb_to_recordset(@Units::jsonb) AS item ("Track" integer)
                    )
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "MediaAssets" AS asset
                        WHERE
                            asset."WorkUnitId" = track."Id"
                    );
                """;
            sql.Parameters.Add(data);
            sql.Parameters.Add(new WorkIdData(workId.Value));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            return workId.Value;
        }

        public static async Task TouchWorkIdentityAsync(this LogicContext ctx, WorkIdentityData data, CancellationToken ct)
        {
            // A provider entry that is gone waits for the next refresh like any other.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WorkExternalIdentities" AS identity
                SET
                    "UpdatedAt" = now()
                WHERE
                    identity."WorkId" = @WorkId
                    AND identity."Namespace" = @Namespace;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
