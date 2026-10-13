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

            sql = new SqlStatement();
            sql.Statement = workId is null
                ? """
                  INSERT INTO
                      "Works" ("MediaTypeId", "CanonicalTitle", "ReleaseYear")
                  VALUES
                      (@MediaTypeId, @CanonicalTitle, @ReleaseYear)
                  RETURNING
                      "Id";
                  """
                : """
                  UPDATE
                      "Works" AS work
                  SET
                      "ReleaseYear" = COALESCE(work."ReleaseYear", @ReleaseYear),
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

            // Provider facts are replaced; manual facts stay.
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
                    AND length(btrim(item."Value")) > 0;
                """;
            sql.Parameters.Add(data);
            sql.Parameters.Add(new WorkIdData(workId.Value));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // Mappings refresh unless corrected by hand; a mapping owned by another Work is left alone.
            sql = new SqlStatement();
            sql.Statement =
                """
                WITH item AS (
                    SELECT
                        "Namespace",
                        "Value"
                    FROM
                        jsonb_to_recordset(@ExternalIdentities::jsonb) AS item ("Namespace" text, "Value" text)
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

            // Seasons first, then their episodes; existing units keep their identity.
            sql = new SqlStatement();
            sql.Statement =
                """
                WITH item AS (
                    SELECT
                        *
                    FROM
                        jsonb_to_recordset(@Units::jsonb) AS item ("Season" integer, "Episode" integer, "Title" text, "ReleasedOn" date)
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
                        item."Episode" IS NULL
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
            return workId.Value;
        }
    }
}
