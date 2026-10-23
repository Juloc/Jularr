using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static partial class FranchiseLogic
    {
        // Open Library names a series in free text, such as "The Dark Tower ; 1", "Discworld, #3" or "Dune Chronicles (Book 2)".
        public static (string Name, decimal? Position)? ReadSeries(string? text)
        {
            if (text is null || text.Trim() is not { Length: > 0 and <= 300 } trimmed)
            {
                return null;
            }

            var match = SeriesText().Match(trimmed);
            return match.Success && match.Groups["name"].Value.Trim(' ', ',', ';', '-', '(') is { Length: > 0 } name
                ? (name, decimal.Parse(match.Groups["position"].Value, CultureInfo.InvariantCulture))
                : (trimmed, null);
        }

        // Open Library's series are known by their name only.
        public static string SeriesKey(string name) => name.Trim().ToLowerInvariant();

        public static async Task SaveWorkFranchiseAsync(this LogicContext ctx, WorkFranchiseData data, CancellationToken ct)
        {
            // The provider decides unless an administrator chose; its series is kept once and renamed with it.
            var sql = new SqlStatement();
            if (data.Namespace is null || data.ExternalId is null || data.Name is null)
            {
                sql.Statement =
                    """
                    UPDATE
                        "Works" AS work
                    SET
                        "FranchiseId" = NULL,
                        "FranchisePosition" = NULL
                    WHERE
                        work."Id" = @WorkId
                        AND work."FranchiseId" IS NOT NULL
                        AND NOT work."IsFranchiseManual";
                    """;
                sql.Parameters.Add(data);
                await ctx.ExecuteAsyncForAffectedRows(sql, ct);
                return;
            }

            sql.Statement =
                """
                WITH known AS (
                    SELECT
                        identity."FranchiseId"
                    FROM
                        "FranchiseExternalIdentities" AS identity
                    WHERE
                        identity."Namespace" = @Namespace
                        AND identity."Value" = @ExternalId
                ), renamed AS (
                    UPDATE
                        "Franchises" AS franchise
                    SET
                        "Name" = @Name
                    FROM
                        known
                    WHERE
                        franchise."Id" = known."FranchiseId"
                        AND franchise."Name" <> @Name
                    RETURNING
                        franchise."Id"
                ), created AS (
                    INSERT INTO
                        "Franchises" ("Name")
                    SELECT
                        @Name
                    WHERE
                        NOT EXISTS (SELECT 1 FROM known)
                    RETURNING
                        "Id"
                ), identified AS (
                    INSERT INTO
                        "FranchiseExternalIdentities" ("FranchiseId", "Namespace", "Value")
                    SELECT
                        created."Id",
                        @Namespace,
                        @ExternalId
                    FROM
                        created
                )
                UPDATE
                    "Works" AS work
                SET
                    "FranchiseId" = COALESCE((SELECT known."FranchiseId" FROM known), (SELECT created."Id" FROM created)),
                    "FranchisePosition" = @Position
                WHERE
                    work."Id" = @WorkId
                    AND NOT work."IsFranchiseManual";
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // An administrator's series: one of that name or a new one; without a name the providers decide again.
        public static async Task ChooseWorkFranchiseAsync(this LogicContext ctx, WorkFranchiseChoiceData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement = data.Name is null
                ? """
                  UPDATE
                      "Works" AS work
                  SET
                      "FranchiseId" = NULL,
                      "FranchisePosition" = NULL,
                      "IsFranchiseManual" = FALSE
                  WHERE
                      work."Id" = @WorkId;
                  """
                : """
                  WITH known AS (
                      SELECT
                          franchise."Id"
                      FROM
                          "Franchises" AS franchise
                      WHERE
                          lower(franchise."Name") = lower(@Name)
                      ORDER BY
                          franchise."Id"
                      LIMIT 1
                  ), created AS (
                      INSERT INTO
                          "Franchises" ("Name")
                      SELECT
                          @Name
                      WHERE
                          NOT EXISTS (SELECT 1 FROM known)
                      RETURNING
                          "Id"
                  )
                  UPDATE
                      "Works" AS work
                  SET
                      "FranchiseId" = COALESCE((SELECT known."Id" FROM known), (SELECT created."Id" FROM created)),
                      "FranchisePosition" = @Position,
                      "IsFranchiseManual" = TRUE
                  WHERE
                      work."Id" = @WorkId;
                  """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The work was not found.");
            }
        }

        [GeneratedRegex(@"^(?<name>.+?)\s*(?:[;,:#]|--|\(|\bbook\b|\bvol(?:ume)?\.?|\bband\b|\bnr\.?)\s*#?\s*(?<position>[0-9]{1,4}(?:\.[0-9]{1,2})?)\s*\)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex SeriesText();
    }
}
