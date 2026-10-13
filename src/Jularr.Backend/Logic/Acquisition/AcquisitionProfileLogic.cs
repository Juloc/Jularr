using System.Text.Json;
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

        public static bool SupportsProfiles(MediaType mediaType) => ProfileQualities.For(mediaType).Count > 0;

        public static string BuildDefinition(AcquisitionProfileInputData input, string? existing)
        {
            // Editor values on top of the stored rules.
            if (!SupportsProfiles(input.MediaTypeId))
            {
                throw new DataValidationException(nameof(input.MediaTypeId), "unknown_catalog_value");
            }

            var order = ProfileQualities.For(input.MediaTypeId);
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

            var stored = existing is null ? null : JsonSerializer.Deserialize<AcquisitionProfileDefinition>(existing, AcquisitionSelectionLogic.DefinitionOptions);
            var definition = new AcquisitionProfileDefinition(allowed, [.. order], input.UpgradeAllowed, cutoff, input.MinimumSizeMb * Megabyte, input.MaximumSizeMb * Megabyte,
                Terms(input.MustContain), Terms(input.MustNotContain), stored?.RequiredRegex ?? [], stored?.RejectedRegex ?? [], stored?.ScoreRules ?? []);
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
            var row = new AcquisitionProfileRowData(null, input.Name, input.MediaTypeId, input.IsDefault, BuildDefinition(input, null), null);
            await ctx.ClearDefaultProfileAsync(row, ct);
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AcquisitionProfiles" ("Name", "MediaTypeId", "IsDefault", "Definition")
                VALUES
                    (@Name, @MediaTypeId, @IsDefault, @Definition::jsonb)
                RETURNING
                    "Id",
                    "Version";
                """;
            sql.Parameters.Add(row);
            return (await ctx.ReadRequiredAsync<AcquisitionProfileKeyData>(sql, ct)).Id;
        }

        public static async Task UpdateAcquisitionProfileAsync(this LogicContext ctx, AcquisitionProfileInputData input, CancellationToken ct)
        {
            // The media type stays; a default is replaced, never removed.
            var state = await ctx.ReadAcquisitionProfileStateAsync(input.Id!.Value, ct);
            if (state.Version != input.Version)
            {
                throw new DataValidationException(nameof(input.Version), "outdated_version");
            }

            if (state.MediaTypeId != input.MediaTypeId || state.IsDefault && !input.IsDefault)
            {
                throw new DataValidationException(state.MediaTypeId != input.MediaTypeId ? nameof(input.MediaTypeId) : nameof(input.IsDefault), "invalid_transition");
            }

            var row = new AcquisitionProfileRowData(state.Id, input.Name, input.MediaTypeId, input.IsDefault, BuildDefinition(input, state.Definition), state.Version);
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
        }

        private static async Task ClearDefaultProfileAsync(this LogicContext ctx, AcquisitionProfileRowData row, CancellationToken ct)
        {
            // One default per media type.
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
                    AND profile."IsDefault"
                    AND profile."Id" IS DISTINCT FROM @Id;
                """;
            sql.Parameters.Add(row);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        private static Task<AcquisitionProfileStateData> ReadAcquisitionProfileStateAsync(this LogicContext ctx, long id, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."MediaTypeId",
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

        private static string[] Terms(string[] values) =>
            [.. values.Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
