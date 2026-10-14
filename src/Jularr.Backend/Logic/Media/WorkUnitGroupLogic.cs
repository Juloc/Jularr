using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class WorkUnitGroupLogic
    {
        public static async Task<long> CreateWorkUnitGroupAsync(this LogicContext ctx, WorkUnitGroupSaveData data, CancellationToken ct)
        {
            // One Work's sections change one at a time.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id" AS "WorkId",
                    work."MediaTypeId"
                FROM
                    "Works" AS work
                WHERE
                    work."Id" = @WorkId
                FOR UPDATE OF
                    work;
                """;
            sql.Parameters.Add(data);
            var work = await ctx.ReadOptionalAsync<WorkLookupData>(sql, ct) ?? throw new KeyNotFoundException("The work was not found.");

            // Sections count the Work's top units: episodes within their season, volumes or tracks; chapters follow their volume.
            var units = WorkUnitTypes.For(work.MediaTypeId);
            if (units.Count == 0)
            {
                throw new DataValidationException(nameof(data.FirstNumber), "invalid_transition");
            }

            var seasons = WorkUnitTypes.ParentOf(units[0]) is not null;
            if (seasons && (data.FirstParentNumber is null || data.LastParentNumber is null))
            {
                throw new DataValidationException(nameof(data.FirstParentNumber), "required");
            }

            if (!seasons && (data.FirstParentNumber is not null || data.LastParentNumber is not null))
            {
                throw new DataValidationException(nameof(data.FirstParentNumber), "out_of_range");
            }

            if ((data.FirstParentNumber ?? 0, data.FirstNumber).CompareTo((data.LastParentNumber ?? 0, data.LastNumber)) > 0)
            {
                throw new DataValidationException(nameof(data.LastNumber), "out_of_range");
            }

            // A unit belongs to at most one section; 100 sections per Work keep the editor complete.
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    unitGroup."Id"
                FROM
                    "WorkUnitGroups" AS unitGroup
                WHERE
                    unitGroup."WorkId" = @WorkId
                    AND (coalesce(@FirstParentNumber, 0), @FirstNumber) <= (coalesce(unitGroup."LastParentNumber", 0), unitGroup."LastNumber")
                    AND (coalesce(unitGroup."FirstParentNumber", 0), unitGroup."FirstNumber") <= (coalesce(@LastParentNumber, 0), @LastNumber)
                LIMIT 1;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<WorkUnitGroupIdData>(sql, ct) is not null)
            {
                throw new DataValidationException(nameof(data.FirstNumber), "overlapping_group");
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id"
                FROM
                    "Works" AS work
                WHERE
                    work."Id" = @WorkId
                    AND (SELECT count(*) FROM "WorkUnitGroups" AS unitGroup WHERE unitGroup."WorkId" = work."Id") < 100;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<WorkIdData>(sql, ct) is null)
            {
                throw new DataValidationException(nameof(data.Name), "group_limit");
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkUnitGroups" ("WorkId", "Name", "FirstParentNumber", "FirstNumber", "LastParentNumber", "LastNumber")
                VALUES
                    (@WorkId, @Name, @FirstParentNumber, @FirstNumber, @LastParentNumber, @LastNumber)
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadRequiredAsync<WorkUnitGroupIdData>(sql, ct)).Id;
        }

        public static async Task DeleteWorkUnitGroupAsync(this LogicContext ctx, WorkUnitGroupKeyData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "WorkUnitGroups" AS unitGroup
                WHERE
                    unitGroup."Id" = @WorkUnitGroupId
                    AND unitGroup."WorkId" = @WorkId;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The section was not found.");
            }
        }
    }
}
