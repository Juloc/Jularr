using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class RequestRuleLogic
    {
        public static async Task<long> CreateRequestRuleAsync(this LogicContext ctx, RequestRuleSaveData data, CancellationToken ct)
        {
            data = await ctx.CheckRequestRuleAsync(data, ct);
            await ctx.ClearDefaultRequestRuleAsync(data, ct);

            // Allowed media are stored as the categories they block, so new categories start allowed.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "RequestRules" ("Name", "Description", "RequestLimit", "PeriodDays", "IsAutomaticApproval", "BlockedMediaKeys", "AcquisitionProfileIds", "IsDefault")
                VALUES
                    (@Name, @Description, @RequestLimit, @PeriodDays, @IsAutomaticApproval,
                        ARRAY(SELECT category."Key" FROM "MediaCategories" AS category WHERE NOT category."Key" = ANY(@AllowedMediaKeys) ORDER BY category."Position"),
                        ARRAY(SELECT DISTINCT item FROM unnest(@AcquisitionProfileIds) AS item ORDER BY item), @IsDefault)
                RETURNING
                    "Id",
                    "Version";
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadRequiredAsync<RequestRuleKeyData>(sql, ct)).Id;
        }

        public static async Task UpdateRequestRuleAsync(this LogicContext ctx, RequestRuleSaveData data, CancellationToken ct)
        {
            // The default stays until another rule takes its place.
            var state = await ctx.ReadRequestRuleStateAsync(data.Id!.Value, ct);
            if (state.IsDefault && !data.IsDefault)
            {
                throw new DataValidationException(nameof(data.IsDefault), "invalid_transition");
            }

            data = await ctx.CheckRequestRuleAsync(data, ct);
            await ctx.ClearDefaultRequestRuleAsync(data, ct);
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "RequestRules" AS rule
                SET
                    "Name" = @Name,
                    "Description" = @Description,
                    "RequestLimit" = @RequestLimit,
                    "PeriodDays" = @PeriodDays,
                    "IsAutomaticApproval" = @IsAutomaticApproval,
                    "BlockedMediaKeys" = ARRAY(SELECT category."Key" FROM "MediaCategories" AS category WHERE NOT category."Key" = ANY(@AllowedMediaKeys) ORDER BY category."Position"),
                    "AcquisitionProfileIds" = ARRAY(SELECT DISTINCT item FROM unnest(@AcquisitionProfileIds) AS item ORDER BY item),
                    "IsDefault" = @IsDefault,
                    "Version" = rule."Version" + 1
                WHERE
                    rule."Id" = @Id
                    AND rule."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "outdated_version");
            }
        }

        public static async Task DeleteRequestRuleAsync(this LogicContext ctx, RequestRuleKeyData data, CancellationToken ct)
        {
            // Accounts on a deleted rule return to the default without the rule's overrides.
            if ((await ctx.ReadRequestRuleStateAsync(data.Id, ct)).IsDefault)
            {
                throw new DataValidationException("IsDefault", "in_use");
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "AccountRequestRules" AS assignment
                WHERE
                    assignment."RequestRuleId" = @Id;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "RequestRules" AS rule
                WHERE
                    rule."Id" = @Id
                    AND rule."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "outdated_version");
            }
        }

        public static async Task SaveAccountRequestRuleAsync(this LogicContext ctx, AccountRequestRuleSaveData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    account."Id"
                FROM
                    "Accounts" AS account
                WHERE
                    account."Id" = @AccountId
                FOR UPDATE OF
                    account;
                """;
            sql.Parameters.Add(data);
            _ = await ctx.ReadOptionalAsync<AccountIdData>(sql, ct) ?? throw new KeyNotFoundException("The account was not found.");
            var check = await ctx.ReadRequestRuleCheckAsync(new RequestRuleReferenceData(null, null, data.AllowedMediaKeys, data.AcquisitionProfileIds), ct);
            if (!check.AreMediaKeysKnown)
            {
                throw new DataValidationException(nameof(data.AllowedMediaKeys), "unknown_catalog_value");
            }

            if (!check.AreProfilesKnown)
            {
                throw new DataValidationException(nameof(data.AcquisitionProfileIds), "unknown_reference");
            }

            // Only values that differ from the rule override it; the rest follows later rule edits.
            sql = new SqlStatement();
            sql.Statement =
                """
                WITH rule AS (
                    SELECT
                        candidate."RequestLimit",
                        candidate."PeriodDays",
                        candidate."IsAutomaticApproval",
                        candidate."BlockedMediaKeys",
                        candidate."AcquisitionProfileIds"
                    FROM
                        "RequestRules" AS candidate
                    WHERE
                        candidate."Id" = @RequestRuleId
                        OR @RequestRuleId::bigint IS NULL AND candidate."IsDefault"
                ), edited AS (
                    SELECT
                        ARRAY(SELECT category."Key" FROM "MediaCategories" AS category WHERE NOT category."Key" = ANY(@AllowedMediaKeys) ORDER BY category."Position") AS "BlockedMediaKeys",
                        ARRAY(SELECT DISTINCT item FROM unnest(@AcquisitionProfileIds) AS item ORDER BY item) AS "AcquisitionProfileIds"
                )
                INSERT INTO
                    "AccountRequestRules" ("AccountId", "RequestRuleId", "HasLimitOverride", "RequestLimit", "PeriodDays", "IsAutomaticApproval", "BlockedMediaKeys", "AcquisitionProfileIds")
                SELECT
                    @AccountId,
                    @RequestRuleId,
                    @IsOverridden AND @RequestLimit IS DISTINCT FROM rule."RequestLimit",
                    CASE WHEN @IsOverridden AND @RequestLimit IS DISTINCT FROM rule."RequestLimit" THEN @RequestLimit END,
                    CASE WHEN @IsOverridden AND @PeriodDays <> rule."PeriodDays" THEN @PeriodDays END,
                    CASE WHEN @IsOverridden AND @IsAutomaticApproval <> rule."IsAutomaticApproval" THEN @IsAutomaticApproval END,
                    CASE WHEN @IsOverridden AND NOT (edited."BlockedMediaKeys" @> rule."BlockedMediaKeys" AND rule."BlockedMediaKeys" @> edited."BlockedMediaKeys") THEN edited."BlockedMediaKeys" END,
                    CASE WHEN @IsOverridden AND NOT (edited."AcquisitionProfileIds" @> rule."AcquisitionProfileIds" AND rule."AcquisitionProfileIds" @> edited."AcquisitionProfileIds")
                        THEN edited."AcquisitionProfileIds" END
                FROM
                    rule
                    CROSS JOIN edited
                ON CONFLICT ("AccountId") DO UPDATE
                SET
                    "RequestRuleId" = EXCLUDED."RequestRuleId",
                    "HasLimitOverride" = EXCLUDED."HasLimitOverride",
                    "RequestLimit" = EXCLUDED."RequestLimit",
                    "PeriodDays" = EXCLUDED."PeriodDays",
                    "IsAutomaticApproval" = EXCLUDED."IsAutomaticApproval",
                    "BlockedMediaKeys" = EXCLUDED."BlockedMediaKeys",
                    "AcquisitionProfileIds" = EXCLUDED."AcquisitionProfileIds";
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.RequestRuleId), "unknown_reference");
            }
        }

        public static async Task<RequestPolicyData> ReadRequestPolicyAsync(this LogicContext ctx, AcquisitionRequestCreationData data, CancellationToken ct)
        {
            // The account row serializes its requests so the limit holds under concurrency.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    account."Id"
                FROM
                    "Accounts" AS account
                WHERE
                    account."Id" = @RequestedByAccountId
                FOR UPDATE OF
                    account;
                """;
            sql.Parameters.Add(data);
            _ = await ctx.ReadRequiredAsync<AccountIdData>(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    rule."RequestLimit",
                    rule."IsAutomaticApproval",
                    category."Key" = ANY(rule."BlockedMediaKeys") AS "IsBlocked",
                    @AcquisitionProfileId::bigint IS NULL OR @AcquisitionProfileId = ANY(rule."AcquisitionProfileIds") AND EXISTS (
                        SELECT
                            1
                        FROM
                            "AcquisitionProfiles" AS profile
                        WHERE
                            profile."Id" = @AcquisitionProfileId
                            AND profile."MediaTypeId" = class."MediaTypeId"
                            AND profile."MediaSubtypeId" IS NOT DISTINCT FROM class."MediaSubtypeId"
                    ) AS "IsProfileAllowed",
                    (
                        SELECT
                            count(*)
                        FROM
                            "AcquisitionRequests" AS request
                        WHERE
                            request."RequestedByAccountId" = account."Id"
                            AND request."CreatedAt" > now() - make_interval(days => rule."PeriodDays")
                    ) AS "RecentCount",
                    (
                        SELECT
                            request."Id"
                        FROM
                            "AcquisitionRequests" AS request
                        WHERE
                            request."RequestedByProfileId" = @RequestedByProfileId
                            AND request."WorkId" = @WorkId
                            AND request."UiLocaleId" = @UiLocaleId
                            AND request."GamePlatformId" IS NOT DISTINCT FROM @GamePlatformId
                            AND request."AcquisitionRequestStatusTypeId" IN (1, 2, 3, 4, 5)
                    ) AS "ActiveRequestId"
                FROM
                    "Accounts" AS account
                    JOIN "EffectiveRequestRules" AS rule ON rule."AccountId" = account."Id"
                    CROSS JOIN "WorkClasses" AS class
                    JOIN "MediaCategories" AS category ON class."MediaSubtypeId" IS NULL AND category."MediaTypeId" = class."MediaTypeId"
                        OR category."MediaSubtypeId" = class."MediaSubtypeId"
                WHERE
                    account."Id" = @RequestedByAccountId
                    AND class."WorkId" = @WorkId;
                """;
            sql.Parameters.Add(data);
            return await ctx.ReadRequiredAsync<RequestPolicyData>(sql, ct);
        }

        private static async Task<RequestRuleSaveData> CheckRequestRuleAsync(this LogicContext ctx, RequestRuleSaveData data, CancellationToken ct)
        {
            var check = await ctx.ReadRequestRuleCheckAsync(new RequestRuleReferenceData(data.Id, data.Name, data.AllowedMediaKeys, data.AcquisitionProfileIds), ct);
            if (check.IsNameTaken)
            {
                throw new DataValidationException(nameof(data.Name), "duplicate_value");
            }

            if (!check.AreMediaKeysKnown)
            {
                throw new DataValidationException(nameof(data.AllowedMediaKeys), "unknown_catalog_value");
            }

            return check.AreProfilesKnown ? data : throw new DataValidationException(nameof(data.AcquisitionProfileIds), "unknown_reference");
        }

        private static Task<RequestRuleCheckData> ReadRequestRuleCheckAsync(this LogicContext ctx, RequestRuleReferenceData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    EXISTS (SELECT 1 FROM "RequestRules" AS rule WHERE lower(rule."Name") = lower(@Name) AND rule."Id" IS DISTINCT FROM @Id) AS "IsNameTaken",
                    (SELECT count(*) FROM "MediaCategories" AS category WHERE category."Key" = ANY(@AllowedMediaKeys))
                        = (SELECT count(DISTINCT item) FROM unnest(@AllowedMediaKeys) AS item) AS "AreMediaKeysKnown",
                    (SELECT count(*) FROM "AcquisitionProfiles" AS profile WHERE profile."Id" = ANY(@AcquisitionProfileIds))
                        = (SELECT count(DISTINCT item) FROM unnest(@AcquisitionProfileIds) AS item) AS "AreProfilesKnown";
                """;
            sql.Parameters.Add(data);
            return ctx.ReadRequiredAsync<RequestRuleCheckData>(sql, ct);
        }

        private static Task<RequestRuleStateData> ReadRequestRuleStateAsync(this LogicContext ctx, long id, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    rule."IsDefault"
                FROM
                    "RequestRules" AS rule
                WHERE
                    rule."Id" = @Id
                FOR UPDATE;
                """;
            sql.Parameters.Add(new RequestRuleKeyData(id, 1));
            return ctx.ReadRequiredAsync<RequestRuleStateData>(sql, ct);
        }

        private static async Task ClearDefaultRequestRuleAsync(this LogicContext ctx, RequestRuleSaveData data, CancellationToken ct)
        {
            // One default rule.
            if (!data.IsDefault)
            {
                return;
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "RequestRules" AS rule
                SET
                    "IsDefault" = FALSE,
                    "Version" = rule."Version" + 1
                WHERE
                    rule."IsDefault"
                    AND rule."Id" IS DISTINCT FROM @Id;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
