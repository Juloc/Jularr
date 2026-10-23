using System.Text.RegularExpressions;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class AcquisitionPolicyLogic
    {
        public static async Task<ReleaseProfileKeyData> SaveReleaseProfileAsync(this LogicContext ctx, ReleaseProfileWriteData data, CancellationToken ct)
        {
            if (data.Required.Length + data.Ignored.Length == 0) throw new DataValidationException(nameof(data.Required), "required");
            ValidateTerms(data.Required, nameof(data.Required));
            ValidateTerms(data.Ignored, nameof(data.Ignored));
            await ctx.RequireAcquisitionTagsAsync(new AcquisitionTagSetData(data.TagIds), ct);
            if (data.AcquisitionConnectionId is { } indexer) await ctx.RequirePolicyIndexerAsync(new PolicyIndexerData(indexer), ct);
            if (data.Id is not null && data.Version is null) throw new DataValidationException(nameof(data.Version), "required");
            var sql = new SqlStatement();
            if (data.Id is null)
            {
                sql.Statement =
                    """
                    INSERT INTO
                        "ReleaseProfiles" ("MediaTypeId", "Name", "IsEnabled", "Required", "Ignored", "AcquisitionConnectionId", "TagIds")
                    VALUES
                        (@MediaTypeId, @Name, @IsEnabled, @Required, @Ignored, @AcquisitionConnectionId, @TagIds)
                    RETURNING
                        "Id",
                        "Version";
                    """;
            }
            else
            {
                sql.Statement =
                    """
                    UPDATE
                        "ReleaseProfiles" AS item
                    SET
                        "MediaTypeId" = @MediaTypeId,
                        "Name" = @Name,
                        "IsEnabled" = @IsEnabled,
                        "Required" = @Required,
                        "Ignored" = @Ignored,
                        "AcquisitionConnectionId" = @AcquisitionConnectionId,
                        "TagIds" = @TagIds,
                        "Version" = item."Version" + 1
                    WHERE
                        item."Id" = @Id
                        AND item."Version" = @Version
                    RETURNING
                        item."Id",
                        item."Version";
                    """;
            }

            sql.Parameters.Add(data);
            return await ctx.ReadOptionalAsync<ReleaseProfileKeyData>(sql, ct) ?? throw new DataValidationException(nameof(data.Version), "outdated_version");
        }

        public static async Task DeleteReleaseProfileAsync(this LogicContext ctx, ReleaseProfileKeyData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    item."Id",
                    item."MediaTypeId",
                    item."Name",
                    item."IsEnabled",
                    item."Required",
                    item."Ignored",
                    item."AcquisitionConnectionId",
                    item."TagIds",
                    item."Version"
                FROM
                    "ReleaseProfiles" AS item
                WHERE
                    item."Id" = @Id
                FOR UPDATE;
                """;
            sql.Parameters.Add(data);
            var current = await ctx.ReadOptionalAsync<ReleaseProfileRowData>(sql, ct);
            if (current?.Version != data.Version) throw new DataValidationException(nameof(data.Version), "outdated_version");

            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "ReleaseProfiles" AS item
                WHERE
                    item."Id" = @Id
                    AND item."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1) throw new DataValidationException(nameof(data.Id), "in_use");
        }

        public static async Task<AcquisitionTagKeyData> SaveAcquisitionTagAsync(this LogicContext ctx, AcquisitionTagWriteData data, CancellationToken ct)
        {
            if (!Regex.IsMatch(data.Label, "^[a-z0-9][a-z0-9 _-]*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) throw new DataValidationException(nameof(data.Label), "invalid_type");
            if (data.Id is not null && data.Version is null) throw new DataValidationException(nameof(data.Version), "required");
            var sql = new SqlStatement();
            if (data.Id is null)
            {
                sql.Statement =
                    """
                    INSERT INTO
                        "Tags" ("Label")
                    VALUES
                        (@Label)
                    RETURNING
                        "Id",
                        "Version";
                    """;
            }
            else
            {
                sql.Statement =
                    """
                    UPDATE
                        "Tags" AS item
                    SET
                        "Label" = @Label,
                        "Version" = item."Version" + 1
                    WHERE
                        item."Id" = @Id
                        AND item."Version" = @Version
                    RETURNING
                        item."Id",
                        item."Version";
                    """;
            }

            sql.Parameters.Add(data);
            return await ctx.ReadOptionalAsync<AcquisitionTagKeyData>(sql, ct) ?? throw new DataValidationException(nameof(data.Version), "outdated_version");
        }

        public static async Task DeleteAcquisitionTagAsync(this LogicContext ctx, AcquisitionTagKeyData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    item."Id",
                    item."Label",
                    item."Version"
                FROM
                    "Tags" AS item
                WHERE
                    item."Id" = @Id
                FOR UPDATE;
                """;
            sql.Parameters.Add(data);
            var current = await ctx.ReadOptionalAsync<AcquisitionTagRowData>(sql, ct);
            if (current?.Version != data.Version) throw new DataValidationException(nameof(data.Version), "outdated_version");

            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "Tags" AS item
                WHERE
                    item."Id" = @Id
                    AND item."Version" = @Version
                    AND NOT EXISTS (SELECT 1 FROM "WorkTags" AS link WHERE link."TagId" = item."Id")
                    AND NOT EXISTS (SELECT 1 FROM "ReleaseProfiles" AS profile WHERE item."Id" = ANY(profile."TagIds"));
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1) throw new DataValidationException(nameof(data.Id), "in_use");
        }

        public static async Task RequireAcquisitionTagsAsync(this LogicContext ctx, AcquisitionTagSetData data, CancellationToken ct)
        {
            if (data.TagIds.Length == 0) return;
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    tag."Id",
                    tag."Version"
                FROM
                    "Tags" AS tag
                WHERE
                    tag."Id" = ANY(@TagIds)
                ORDER BY
                    tag."Id"
                FOR SHARE;
                """;
            sql.Parameters.Add(data);
            var tags = await ctx.Sql.ReadListAsync<AcquisitionTagKeyData>(sql, ct);
            if (tags.Count != data.TagIds.Length) throw new DataValidationException(nameof(data.TagIds), "unknown_reference");
        }

        private static async Task RequirePolicyIndexerAsync(this LogicContext ctx, PolicyIndexerData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    connection."Id"
                FROM
                    "AcquisitionConnections" AS connection
                    JOIN "AcquisitionConnectionTypes" AS type ON type."Id" = connection."AcquisitionConnectionTypeId"
                WHERE
                    connection."Id" = @Id
                    AND type."AcquisitionConnectionRoleId" = 1
                FOR SHARE OF
                    connection;
                """;
            sql.Parameters.Add(data);
            if (await ctx.Sql.ExecuteScalarAsync(sql, ct) is null) throw new DataValidationException("AcquisitionConnectionId", "unknown_reference");
        }

        private static void ValidateTerms(IEnumerable<string> terms, string field)
        {
            foreach (var term in terms.Where(term => term.StartsWith('/')))
            {
                var end = term.LastIndexOf('/');
                if (end < 2 || term[(end + 1)..] is not ("" or "i")) throw new DataValidationException(field, "invalid_type");
                try
                {
                    _ = new Regex(term[1..end], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException)
                {
                    throw new DataValidationException(field, "invalid_type");
                }
            }
        }
    }
}
