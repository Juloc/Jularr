using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class AcquisitionProfileLogic
    {
        private const long Megabyte = 1024L * 1024L;

        public static bool SupportsProfiles(MediaType mediaType, MediaSubtype? subtype) => MediaSubtypes.Fits(mediaType, subtype) && ProfileQualities.For(mediaType, subtype).Count > 0;

        public static string BuildDefinition(AcquisitionProfileInputData input, string? existing)
        {
            // Editor values on top of the stored rules.
            if (!SupportsProfiles(input.MediaTypeId, input.MediaSubtypeId))
            {
                throw new DataValidationException(nameof(input.MediaTypeId), "unknown_catalog_value");
            }

            var order = ProfileQualities.For(input.MediaTypeId, input.MediaSubtypeId);
            var allowed = order.Where(quality => input.AllowedQualities.Contains(quality, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (allowed.Length == 0 || allowed.Length != input.AllowedQualities.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            {
                throw new DataValidationException(nameof(input.AllowedQualities), allowed.Length == 0 ? "required" : "unknown_catalog_value");
            }

            var cutoff = input.UpgradeAllowed ? allowed.FirstOrDefault(quality => string.Equals(quality, input.UpgradeCutoffQuality, StringComparison.OrdinalIgnoreCase)) : null;
            if (input.UpgradeAllowed && cutoff is null)
            {
                throw new DataValidationException(nameof(input.UpgradeCutoffQuality), "unknown_catalog_value");
            }

            if (input.MinimumSizeMb is { } minimum && input.MaximumSizeMb is { } maximum && minimum > maximum)
            {
                throw new DataValidationException(nameof(input.MaximumSizeMb), "out_of_range");
            }

            // Fallback qualities are other known qualities and need the hours after which they count.
            var fallback = order.Where(quality => input.FallbackQualities.Contains(quality, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (fallback.Length != input.FallbackQualities.Distinct(StringComparer.OrdinalIgnoreCase).Count() || fallback.Intersect(allowed, StringComparer.OrdinalIgnoreCase).Any())
            {
                throw new DataValidationException(nameof(input.FallbackQualities), "unknown_catalog_value");
            }

            if ((fallback.Length > 0) != (input.FallbackAfterHours is not null))
            {
                throw new DataValidationException(nameof(input.FallbackAfterHours), "required");
            }

            var stored = existing is null ? null : JsonSerializer.Deserialize<AcquisitionProfileDefinition>(existing, AcquisitionSelectionLogic.DefinitionOptions);
            var definition = new AcquisitionProfileDefinition(allowed, [.. order], input.UpgradeAllowed, cutoff, input.MinimumSizeMb * Megabyte, input.MaximumSizeMb * Megabyte,
                Terms(input.MustContain), Terms(input.MustNotContain), stored?.RequiredRegex ?? [], stored?.RejectedRegex ?? [], stored?.ScoreRules ?? [], input.DelayMinutes, fallback,
                fallback.Length > 0 ? input.FallbackAfterHours : null);
            var profile = new QualityProfile("0", input.Name, definition.AllowedQualities, definition.QualityOrder, definition.UpgradeAllowed, definition.UpgradeCutoffQuality,
                definition.MinimumSizeBytes, definition.MaximumSizeBytes, definition.MustContain, definition.MustNotContain, definition.RequiredRegex, definition.RejectedRegex, definition.ScoreRules);
            if (ReleaseScorer.ValidateProfile(profile).Count > 0)
            {
                throw new DataValidationException(nameof(input.AllowedQualities), "invalid_transition");
            }

            return JsonSerializer.Serialize(definition, AcquisitionSelectionLogic.DefinitionOptions);
        }

        public static async Task<long> CreateAcquisitionProfileAsync(this LogicContext ctx, AcquisitionProfileInputData input, CancellationToken ct)
        {
            var row = new AcquisitionProfileRowData(null, input.Name, input.MediaTypeId, input.MediaSubtypeId, input.IsDefault, BuildDefinition(input, null), null);
            await ctx.ClearDefaultProfileAsync(row, ct);
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AcquisitionProfiles" ("Name", "MediaTypeId", "MediaSubtypeId", "IsDefault", "Definition")
                VALUES
                    (@Name, @MediaTypeId, @MediaSubtypeId, @IsDefault, @Definition::jsonb)
                RETURNING
                    "Id",
                    "Version";
                """;
            sql.Parameters.Add(row);
            return (await ctx.ReadRequiredAsync<AcquisitionProfileKeyData>(sql, ct)).Id;
        }

        public static async Task UpdateAcquisitionProfileAsync(this LogicContext ctx, AcquisitionProfileInputData input, CancellationToken ct)
        {
            // Type and subtype stay; a default is replaced, never removed.
            var state = await ctx.ReadAcquisitionProfileStateAsync(input.Id!.Value, ct);
            if (state.Version != input.Version)
            {
                throw new DataValidationException(nameof(input.Version), "outdated_version");
            }

            if (state.MediaTypeId != input.MediaTypeId || state.MediaSubtypeId != input.MediaSubtypeId)
            {
                throw new DataValidationException(nameof(input.MediaTypeId), "invalid_transition");
            }

            if (state.IsDefault && !input.IsDefault)
            {
                throw new DataValidationException(nameof(input.IsDefault), "invalid_transition");
            }

            var row = new AcquisitionProfileRowData(state.Id, input.Name, input.MediaTypeId, input.MediaSubtypeId, input.IsDefault, BuildDefinition(input, state.Definition), state.Version);
            await ctx.ClearDefaultProfileAsync(row, ct);
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AcquisitionProfiles" AS profile
                SET
                    "Name" = @Name,
                    "IsDefault" = @IsDefault,
                    "Definition" = @Definition::jsonb,
                    "Version" = profile."Version" + 1
                WHERE
                    profile."Id" = @Id
                    AND profile."Version" = @Version;
                """;
            sql.Parameters.Add(row);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(input.Version), "outdated_version");
            }
        }

        public static async Task DeleteAcquisitionProfileAsync(this LogicContext ctx, AcquisitionProfileKeyData data, CancellationToken ct)
        {
            // Targets using it fall back to the default.
            var state = await ctx.ReadAcquisitionProfileStateAsync(data.Id, ct);
            if (state.IsDefault)
            {
                throw new DataValidationException("IsDefault", "in_use");
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "AcquisitionProfiles" AS profile
                WHERE
                    profile."Id" = @Id
                    AND profile."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "outdated_version");
            }

            // Request rules stop offering it.
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "RequestRules" AS rule
                SET
                    "AcquisitionProfileIds" = array_remove(rule."AcquisitionProfileIds", @Id),
                    "Version" = rule."Version" + 1
                WHERE
                    @Id = ANY(rule."AcquisitionProfileIds");
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AccountRequestRules" AS assignment
                SET
                    "AcquisitionProfileIds" = array_remove(assignment."AcquisitionProfileIds", @Id)
                WHERE
                    @Id = ANY(assignment."AcquisitionProfileIds");
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        private static async Task ClearDefaultProfileAsync(this LogicContext ctx, AcquisitionProfileRowData row, CancellationToken ct)
        {
            // One default per media type and subtype.
            if (!row.IsDefault)
            {
                return;
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AcquisitionProfiles" AS profile
                SET
                    "IsDefault" = FALSE,
                    "Version" = profile."Version" + 1
                WHERE
                    profile."MediaTypeId" = @MediaTypeId
                    AND profile."MediaSubtypeId" IS NOT DISTINCT FROM @MediaSubtypeId
                    AND profile."IsDefault"
                    AND profile."Id" IS DISTINCT FROM @Id;
                """;
            sql.Parameters.Add(row);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task<IReadOnlyList<ReleaseScoreRule>> ReadProfileRulesAsync(this LogicContext ctx, long id, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."MediaTypeId",
                    profile."MediaSubtypeId",
                    profile."IsDefault",
                    profile."Definition"::text AS "Definition",
                    profile."Version"
                FROM
                    "AcquisitionProfiles" AS profile
                WHERE
                    profile."Id" = @Id;
                """;
            sql.Parameters.Add(new AcquisitionProfileKeyData(id));
            return Definition(await ctx.ReadRequiredAsync<AcquisitionProfileStateData>(sql, ct)).ScoreRules;
        }

        // A profile keeps up to 50 rules.
        public static Task AddProfileRuleAsync(this LogicContext ctx, AcquisitionProfileKeyData key, ReleaseScoreRule rule, CancellationToken ct)
        {
            var stored = Checked(rule);
            return ctx.ReplaceProfileRulesAsync(key, rules => rules.Count < 50 ? [.. rules, stored] : throw new DataValidationException("Name", "out_of_range"), ct);
        }

        // A rule changes in place and keeps its place and id.
        public static Task UpdateProfileRuleAsync(this LogicContext ctx, AcquisitionProfileKeyData key, int ruleId, ReleaseScoreRule rule, CancellationToken ct)
        {
            var stored = Checked(rule);
            return ctx.ReplaceProfileRulesAsync(key, rules => rules.Any(existing => existing.Id == ruleId)
                ? [.. rules.Select(existing => existing.Id == ruleId ? new ReleaseScoreRule(stored.Name, stored.Field, stored.Match, stored.Value, stored.Score) { Effect = stored.Effect, Id = ruleId } : existing)]
                : throw new KeyNotFoundException("The rule was not found."), ct);
        }

        // Rules taken over with a new profile follow its own, as far as the 50 allow.
        public static async Task AppendProfileRulesAsync(this LogicContext ctx, long id, IReadOnlyList<ReleaseScoreRule> rules, CancellationToken ct)
        {
            var added = rules.Select(Checked).ToArray();
            var state = await ctx.ReadAcquisitionProfileStateAsync(id, ct);
            await ctx.ReplaceProfileRulesAsync(new AcquisitionProfileKeyData(id, state.Version), existing => [.. existing.Concat(added).Take(50)], ct);
        }

        // Prefer scores up and Avoid down; the other effects score nothing.
        private static ReleaseScoreRule Checked(ReleaseScoreRule rule)
        {
            if (rule.EffectiveEffect is ReleaseRuleEffect.Prefer or ReleaseRuleEffect.Avoid && rule.Score == 0)
            {
                throw new DataValidationException("Score", "required");
            }

            if (rule.Match == ReleaseRuleMatch.Regex)
            {
                try
                {
                    _ = new Regex(rule.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException)
                {
                    throw new DataValidationException("Value", "invalid_type");
                }
            }

            var score = rule.EffectiveEffect switch
            {
                ReleaseRuleEffect.Prefer => Math.Abs(rule.Score),
                ReleaseRuleEffect.Avoid => -Math.Abs(rule.Score),
                _ => 0
            };
            return new ReleaseScoreRule(rule.Name, rule.Field, rule.Match, rule.Value, score) { Effect = rule.EffectiveEffect };
        }

        public static Task RemoveProfileRuleAsync(this LogicContext ctx, AcquisitionProfileKeyData key, int ruleId, CancellationToken ct) =>
            ctx.ReplaceProfileRulesAsync(key, rules => rules.Any(rule => rule.Id == ruleId) ? [.. rules.Where(rule => rule.Id != ruleId)] : throw new KeyNotFoundException("The rule was not found."), ct);

        // New rules take the next free id of their profile.
        private static ReleaseScoreRule[] Numbered(ReleaseScoreRule[] rules)
        {
            var next = rules.Select(rule => rule.Id).DefaultIfEmpty(0).Max();
            return [.. rules.Select(rule => rule.Id > 0 ? rule
                : new ReleaseScoreRule(rule.Name, rule.Field, rule.Match, rule.Value, rule.Score) { Effect = rule.Effect, Id = ++next })];
        }

        private static async Task ReplaceProfileRulesAsync(this LogicContext ctx, AcquisitionProfileKeyData key, Func<IReadOnlyList<ReleaseScoreRule>, ReleaseScoreRule[]> change,
            CancellationToken ct)
        {
            var state = await ctx.ReadAcquisitionProfileStateAsync(key.Id, ct);
            if (state.Version != key.Version)
            {
                throw new DataValidationException("Version", "outdated_version");
            }

            var stored = Definition(state);
            var definition = new AcquisitionProfileDefinition(stored.AllowedQualities, stored.QualityOrder, stored.UpgradeAllowed, stored.UpgradeCutoffQuality, stored.MinimumSizeBytes,
                stored.MaximumSizeBytes, stored.MustContain, stored.MustNotContain, stored.RequiredRegex, stored.RejectedRegex, Numbered(change(stored.ScoreRules)), stored.DelayMinutes,
                stored.FallbackQualities, stored.FallbackAfterHours);
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AcquisitionProfiles" AS profile
                SET
                    "Definition" = @Definition::jsonb,
                    "Version" = profile."Version" + 1
                WHERE
                    profile."Id" = @Id
                    AND profile."Version" = @Version;
                """;
            sql.Parameters.Add(new AcquisitionProfileDefinitionData(state.Id, JsonSerializer.Serialize(definition, AcquisitionSelectionLogic.DefinitionOptions), state.Version));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        private static AcquisitionProfileDefinition Definition(AcquisitionProfileStateData state) =>
            JsonSerializer.Deserialize<AcquisitionProfileDefinition>(state.Definition, AcquisitionSelectionLogic.DefinitionOptions)
                ?? throw new InvalidOperationException("The acquisition profile is invalid.");

        private static Task<AcquisitionProfileStateData> ReadAcquisitionProfileStateAsync(this LogicContext ctx, long id, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."MediaTypeId",
                    profile."MediaSubtypeId",
                    profile."IsDefault",
                    profile."Definition"::text AS "Definition",
                    profile."Version"
                FROM
                    "AcquisitionProfiles" AS profile
                WHERE
                    profile."Id" = @Id
                FOR UPDATE;
                """;
            sql.Parameters.Add(new AcquisitionProfileKeyData(id));
            return ctx.ReadRequiredAsync<AcquisitionProfileStateData>(sql, ct);
        }

        private static string[] Terms(string[] values) => [.. values.Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
