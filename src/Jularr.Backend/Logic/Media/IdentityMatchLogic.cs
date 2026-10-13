using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class IdentityMatchLogic
    {
        public static async Task SaveIdentityMatchAsync(this LogicContext ctx, IdentityMatchResultData data, CancellationToken ct)
        {
            // An entry another Work already holds stays there; the match then needs a person.
            var status = data.IdentityMatchStatusTypeId;
            SqlStatement sql;
            if (data.Value is not null)
            {
                sql = new SqlStatement();
                sql.Statement =
                    """
                    INSERT INTO
                        "WorkExternalIdentities" ("WorkId", "Namespace", "Value", "IsManual")
                    VALUES
                        (@WorkId, @Namespace, @Value, @IsManual)
                    ON CONFLICT DO NOTHING;
                    """;
                sql.Parameters.Add(data);
                if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 0)
                {
                    status = IdentityMatchStatusType.Review;
                }
            }

            // Season entries land on the Work's regular seasons by number.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkUnitExternalIdentities" ("WorkUnitId", "Namespace", "Value", "EpisodeOffset", "IsManual")
                SELECT
                    season."Id",
                    @Namespace,
                    item."Value",
                    item."Offset",
                    @IsManual
                FROM
                    jsonb_to_recordset(@Seasons::jsonb) AS item ("Season" integer, "Value" text, "Offset" integer)
                    JOIN "WorkUnits" AS season ON season."WorkId" = @WorkId
                        AND season."WorkUnitTypeId" = 1
                        AND season."ParentWorkUnitId" IS NULL
                        AND season."Number" = item."Season"
                ON CONFLICT ("Namespace", "Value") DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // Season entries another Work holds leave this one waiting for a person.
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    EXISTS (
                        SELECT
                            1
                        FROM
                            jsonb_to_recordset(@Seasons::jsonb) AS item ("Season" integer, "Value" text, "Offset" integer)
                        WHERE
                            NOT EXISTS (
                                SELECT
                                    1
                                FROM
                                    "WorkUnitExternalIdentities" AS mapped
                                    JOIN "WorkUnits" AS season ON season."Id" = mapped."WorkUnitId"
                                WHERE
                                    mapped."Namespace" = @Namespace
                                    AND mapped."Value" = item."Value"
                                    AND season."WorkId" = @WorkId
                            )
                    );
                """;
            sql.Parameters.Add(data);
            if ((bool)(await ctx.ReadSql.ExecuteScalarAsync(sql, ct))!)
            {
                status = IdentityMatchStatusType.Review;
            }

            // The latest pass replaces the previous one.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkIdentityMatches" ("WorkId", "Namespace", "IdentityMatchStatusTypeId", "Candidates", "CheckedAt")
                VALUES
                    (@WorkId, @Namespace, @IdentityMatchStatusTypeId, @Candidates::jsonb, now())
                ON CONFLICT ("WorkId", "Namespace") DO UPDATE
                SET
                    "IdentityMatchStatusTypeId" = EXCLUDED."IdentityMatchStatusTypeId",
                    "Candidates" = EXCLUDED."Candidates",
                    "CheckedAt" = EXCLUDED."CheckedAt";
                """;
            sql.Parameters.Add(status == data.IdentityMatchStatusTypeId ? data
                : new IdentityMatchResultData(data.WorkId, data.Namespace, status, data.Candidates, data.Value, data.Seasons, data.IsManual));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
