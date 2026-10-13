using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class WorkMetadataLogic
    {
        public static async Task<long> SaveWorkMetadataAsync(this LogicContext ctx, WorkMetadataData data, CancellationToken ct)
        {
            // The provider identity finds the Work again; manual edits stay.
            var sql = new SqlStatement();
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
            var workId = (await ctx.ReadOptionalAsync<WorkIdData>(sql, ct))?.Id;

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
                      "Works" ("MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear")
                  VALUES
                      (@MediaTypeId, @MediaSubtypeId, @CanonicalTitle, @ReleaseYear)
                  RETURNING
                      "Id";
                  """
                : """
                  UPDATE
                      "Works" AS work
                  SET
                      "ReleaseYear" = COALESCE(work."ReleaseYear", @ReleaseYear),
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
                    item."Value",
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
                        jsonb_to_recordset(@Units::jsonb) AS item ("Season" integer, "Episode" integer, "Volume" integer, "Track" integer, "Title" text, "ReleasedOn" date)
                ), leaves AS (
                    INSERT INTO
                        "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "Title", "ReleasedOn")
                    SELECT
                        @Id,
                        NULL,
                        CASE WHEN item."Volume" IS NOT NULL THEN 3 ELSE 5 END,
                        COALESCE(item."Volume", item."Track"),
                        NULLIF(btrim(item."Title"), ''),
                        item."ReleasedOn"
                    FROM
                        item
                    WHERE
                        item."Volume" IS NOT NULL
                        OR item."Track" IS NOT NULL
                    ON CONFLICT ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") DO UPDATE
                    SET
                        "Title" = COALESCE(EXCLUDED."Title", "WorkUnits"."Title"),
                        "ReleasedOn" = COALESCE(EXCLUDED."ReleasedOn", "WorkUnits"."ReleasedOn")
                ), seasons AS (
                    INSERT INTO
                        "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "Title", "ReleasedOn")
                    SELECT
                        @Id,
                        NULL,
                        1,
                        item."Season",
                        NULLIF(btrim(item."Title"), ''),
                        item."ReleasedOn"
                    FROM
                        item
                    WHERE
                        item."Season" IS NOT NULL
                        AND item."Episode" IS NULL
                    ON CONFLICT ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") DO UPDATE
                    SET
                        "Title" = COALESCE(EXCLUDED."Title", "WorkUnits"."Title"),
                        "ReleasedOn" = EXCLUDED."ReleasedOn"
                    RETURNING
                        "Id",
                        "Number"
                )
                INSERT INTO
                    "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "Title", "ReleasedOn")
                SELECT
                    @Id,
                    seasons."Id",
                    2,
                    item."Episode",
                    NULLIF(btrim(item."Title"), ''),
                    item."ReleasedOn"
                FROM
                    item
                    JOIN seasons ON seasons."Number" = item."Season"
                WHERE
                    item."Episode" IS NOT NULL
                ON CONFLICT ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") DO UPDATE
                SET
                    "Title" = COALESCE(EXCLUDED."Title", "WorkUnits"."Title"),
                    "ReleasedOn" = EXCLUDED."ReleasedOn";
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
