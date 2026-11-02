using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Integrations.Data.Common;

namespace Jularr.Backend.Logic.Media
{
    // People of a Work as its provider credits them; one person per provider id, people without one stay with their Work.
    internal static class WorkPeopleLogic
    {
        private const int PerRole = 50;

        // Rows in the provider's order per role, for the Work, one of its units or one of its catalog editions.
        public static IEnumerable<object> Rows(IEnumerable<ProviderPerson> people, WorkUnitType? unitType = null, decimal? parentNumber = null, decimal? number = null,
            string? editionLanguage = null, EditionFormat? editionFormat = null) =>
            people.Where(person => !string.IsNullOrWhiteSpace(person.Name) && Enum.IsDefined(typeof(PersonRole), person.Role))
                .GroupBy(person => person.Role)
                .SelectMany(role => role.Take(PerRole).Select((person, index) => (object)new
                {
                    ExternalId = person.Id is { Length: > 0 and <= 64 } id ? id : null,
                    Name = TextLimit.Cut(person.Name.Trim(), 300),
                    ImageUrl = person.ImageUrl is { Length: <= 500 } image && image.StartsWith("https://", StringComparison.Ordinal) ? image : null,
                    PersonRoleId = (int)Enum.Parse<PersonRole>(person.Role),
                    Position = index + 1,
                    Credit = string.IsNullOrWhiteSpace(person.Credit) ? null : TextLimit.Cut(person.Credit.Trim(), 300),
                    WorkUnitTypeId = (int?)unitType,
                    ParentNumber = parentNumber,
                    Number = number,
                    EditionLanguage = editionLanguage,
                    EditionFormatId = (int?)editionFormat
                }));

        public static WorkPeopleData Data(long workId, string @namespace, IEnumerable<object> rows) => new(workId, @namespace, JsonSerializer.Serialize(rows));

        // The provider's people no person holds by its id yet.
        public static Task<IReadOnlyList<ProviderPersonIdData>> ReadUnknownProviderPeopleAsync(this LogicContext ctx, ProviderPeopleData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT DISTINCT
                    item."ExternalId"
                FROM
                    jsonb_to_recordset(@People::jsonb) AS item ("ExternalId" text)
                WHERE
                    item."ExternalId" IS NOT NULL
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "PersonExternalIdentities" AS identity
                        WHERE
                            identity."Namespace" = @Namespace
                            AND identity."Value" = item."ExternalId"
                    );
                """;
            sql.Parameters.Add(data);
            return ctx.ReadSql.ReadListAsync<ProviderPersonIdData>(sql, ct);
        }

        // People just looked up by the provider's id: one another catalog already knows by the same TMDB or IMDb id takes the provider's id, the others become
        // people with the provider's id and those TMDB and IMDb ids no person holds yet. Each person is looked up once, since its id is known afterwards.
        public static async Task LinkProviderPeopleAsync(this LogicContext ctx, ProviderPeopleData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH link AS (
                    SELECT DISTINCT ON (item."ExternalId")
                        item."ExternalId",
                        left(btrim(item."Name"), 300) AS "Name",
                        CASE WHEN item."ImageUrl" ~ '^https://' AND length(item."ImageUrl") <= 500 THEN item."ImageUrl" END AS "ImageUrl",
                        CASE WHEN item."TmdbId" ~ '^[1-9][0-9]{0,9}$' THEN item."TmdbId" END AS "TmdbId",
                        CASE WHEN item."ImdbId" ~ '^nm[0-9]{5,10}$' THEN item."ImdbId" END AS "ImdbId"
                    FROM
                        jsonb_to_recordset(@People::jsonb) AS item ("ExternalId" text, "Name" text, "ImageUrl" text, "TmdbId" text, "ImdbId" text)
                    WHERE
                        item."ExternalId" IS NOT NULL
                        AND length(btrim(item."Name")) > 0
                        AND NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "PersonExternalIdentities" AS own
                            WHERE
                                own."Namespace" = @Namespace
                                AND own."Value" = item."ExternalId"
                        )
                    ORDER BY
                        item."ExternalId"
                ), matched AS (
                    SELECT DISTINCT ON (link."ExternalId")
                        link."ExternalId",
                        known."PersonId"
                    FROM
                        link
                        JOIN "PersonExternalIdentities" AS known ON known."Namespace" = 'tmdb' AND known."Value" = link."TmdbId"
                            OR known."Namespace" = 'imdb' AND known."Value" = link."ImdbId"
                    ORDER BY
                        link."ExternalId",
                        known."Namespace" = 'tmdb' DESC,
                        known."PersonId"
                ), fresh AS (
                    SELECT
                        link."ExternalId",
                        link."Name",
                        link."ImageUrl",
                        link."TmdbId",
                        link."ImdbId",
                        nextval(pg_get_serial_sequence('"People"', 'Id')) AS "PersonId"
                    FROM
                        link
                    WHERE
                        NOT EXISTS (SELECT 1 FROM matched WHERE matched."ExternalId" = link."ExternalId")
                ), inserted AS (
                    INSERT INTO
                        "People" ("Id", "Name", "ImageUrl")
                    SELECT
                        fresh."PersonId",
                        fresh."Name",
                        fresh."ImageUrl"
                    FROM
                        fresh
                )
                INSERT INTO
                    "PersonExternalIdentities" ("PersonId", "Namespace", "Value")
                SELECT
                    matched."PersonId",
                    @Namespace,
                    matched."ExternalId"
                FROM
                    matched
                UNION ALL
                SELECT
                    fresh."PersonId",
                    @Namespace,
                    fresh."ExternalId"
                FROM
                    fresh
                UNION ALL
                SELECT
                    fresh."PersonId",
                    remote."Namespace",
                    remote."Value"
                FROM
                    fresh
                    CROSS JOIN LATERAL (VALUES ('tmdb', fresh."TmdbId"), ('imdb', fresh."ImdbId")) AS remote ("Namespace", "Value")
                WHERE
                    remote."Value" IS NOT NULL
                ON CONFLICT DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task SaveWorkPeopleAsync(this LogicContext ctx, WorkPeopleData data, CancellationToken ct)
        {
            // Known ids keep their person, whose name and portrait the provider updates; new ids get one.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH incoming AS (
                    SELECT DISTINCT ON (item."ExternalId")
                        item."ExternalId",
                        item."Name",
                        item."ImageUrl"
                    FROM
                        jsonb_to_recordset(@People::jsonb) AS item ("ExternalId" text, "Name" text, "ImageUrl" text)
                    WHERE
                        item."ExternalId" IS NOT NULL
                    ORDER BY
                        item."ExternalId"
                ), updated AS (
                    UPDATE
                        "People" AS person
                    SET
                        "Name" = incoming."Name",
                        "ImageUrl" = COALESCE(incoming."ImageUrl", person."ImageUrl"),
                        "UpdatedAt" = now()
                    FROM
                        incoming
                        JOIN "PersonExternalIdentities" AS identity ON identity."Namespace" = @Namespace AND identity."Value" = incoming."ExternalId"
                    WHERE
                        person."Id" = identity."PersonId"
                        AND (person."Name" IS DISTINCT FROM incoming."Name" OR incoming."ImageUrl" IS DISTINCT FROM person."ImageUrl" AND incoming."ImageUrl" IS NOT NULL)
                    RETURNING
                        person."Id"
                ), fresh AS (
                    SELECT
                        incoming."ExternalId",
                        incoming."Name",
                        incoming."ImageUrl",
                        nextval(pg_get_serial_sequence('"People"', 'Id')) AS "PersonId"
                    FROM
                        incoming
                    WHERE
                        NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "PersonExternalIdentities" AS identity
                            WHERE
                                identity."Namespace" = @Namespace
                                AND identity."Value" = incoming."ExternalId"
                        )
                ), inserted AS (
                    INSERT INTO
                        "People" ("Id", "Name", "ImageUrl")
                    SELECT
                        fresh."PersonId",
                        fresh."Name",
                        fresh."ImageUrl"
                    FROM
                        fresh
                )
                INSERT INTO
                    "PersonExternalIdentities" ("PersonId", "Namespace", "Value")
                SELECT
                    fresh."PersonId",
                    @Namespace,
                    fresh."ExternalId"
                FROM
                    fresh;

                -- The last import's rows go; people without an id that only this Work named go with them.
                WITH removed AS (
                    DELETE FROM
                        "WorkPeople" AS credit
                    WHERE
                        credit."WorkId" = @Id
                    RETURNING
                        credit."PersonId"
                )
                DELETE FROM
                    "People" AS person
                USING
                    removed
                WHERE
                    person."Id" = removed."PersonId"
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "PersonExternalIdentities" AS identity
                        WHERE
                            identity."PersonId" = person."Id"
                    )
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "WorkPeople" AS other
                        WHERE
                            other."PersonId" = person."Id"
                            AND other."WorkId" <> @Id
                    );

                -- A unit's or an edition's people need it; people without an id are new each time.
                WITH item AS (
                    SELECT
                        row_number() OVER () AS "Row",
                        entry."ExternalId",
                        entry."Name",
                        entry."ImageUrl",
                        entry."PersonRoleId",
                        entry."Position",
                        entry."Credit",
                        unit."Id" AS "WorkUnitId",
                        edition."Id" AS "WorkEditionId"
                    FROM
                        jsonb_to_recordset(@People::jsonb) AS entry ("ExternalId" text, "Name" text, "ImageUrl" text, "PersonRoleId" smallint, "Position" smallint, "Credit" text,
                            "WorkUnitTypeId" smallint, "ParentNumber" numeric, "Number" numeric, "EditionLanguage" text, "EditionFormatId" smallint)
                        LEFT JOIN "WorkEditions" AS edition ON edition."WorkId" = @Id AND edition."Name" IS NULL AND edition."LanguageCode" = entry."EditionLanguage"
                            AND edition."EditionFormatId" = entry."EditionFormatId"
                        LEFT JOIN LATERAL (
                            SELECT
                                unit."Id"
                            FROM
                                "WorkUnits" AS unit
                                LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                            WHERE
                                unit."WorkId" = @Id
                                AND unit."WorkUnitTypeId" = entry."WorkUnitTypeId"
                                AND unit."Number" = entry."Number"
                                AND parent."Number" IS NOT DISTINCT FROM entry."ParentNumber"
                            LIMIT 1
                        ) AS unit ON TRUE
                    WHERE
                        (entry."Number" IS NULL OR unit."Id" IS NOT NULL)
                        AND (entry."EditionFormatId" IS NULL OR edition."Id" IS NOT NULL)
                ), loose AS (
                    SELECT
                        item."Row",
                        item."Name",
                        item."ImageUrl",
                        nextval(pg_get_serial_sequence('"People"', 'Id')) AS "PersonId"
                    FROM
                        item
                    WHERE
                        item."ExternalId" IS NULL
                ), inserted AS (
                    INSERT INTO
                        "People" ("Id", "Name", "ImageUrl")
                    SELECT
                        loose."PersonId",
                        loose."Name",
                        loose."ImageUrl"
                    FROM
                        loose
                )
                INSERT INTO
                    "WorkPeople" ("WorkId", "WorkUnitId", "WorkEditionId", "PersonId", "PersonRoleId", "Position", "Credit")
                SELECT
                    @Id,
                    item."WorkUnitId",
                    item."WorkEditionId",
                    COALESCE(identity."PersonId", loose."PersonId"),
                    item."PersonRoleId",
                    item."Position",
                    item."Credit"
                FROM
                    item
                    LEFT JOIN loose ON loose."Row" = item."Row"
                    LEFT JOIN "PersonExternalIdentities" AS identity ON identity."Namespace" = @Namespace AND identity."Value" = item."ExternalId"
                ON CONFLICT DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
