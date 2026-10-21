using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class AcquisitionProfileLogic
    {
        private const long Megabyte = 1024L * 1024L;

        public static bool SupportsProfiles(MediaType mediaType, MediaSubtype? subtype) => MediaSubtypes.Fits(mediaType, subtype) && ProfileQualities.For(mediaType, subtype).Count > 0;

        public static string BuildDefinition(AcquisitionProfileInputData input, string? existing)
        {
            input.Validate();
            if (!SupportsProfiles(input.MediaTypeId, input.MediaSubtypeId)) throw new DataValidationException(nameof(input.MediaTypeId), "unknown_catalog_value");
            if (input.MinimumSizeMb is { } minimum && input.MaximumSizeMb is { } maximum && minimum > maximum) throw new DataValidationException(nameof(input.MaximumSizeMb), "out_of_range");

            var catalog = ProfileQualities.For(input.MediaTypeId, input.MediaSubtypeId);
            var grouped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var groupIds = new HashSet<long>();
            if (input.QualityGroups.Length > 32) throw new DataValidationException(nameof(input.QualityGroups), "out_of_range");
            foreach (var group in input.QualityGroups)
            {
                if (string.IsNullOrWhiteSpace(group.Name) || group.Name.Length > 128 || group.Id < 1
                    || !groupIds.Add(group.Id) || group.Qualities.Length == 0) throw new DataValidationException(nameof(input.QualityGroups), "invalid_transition");
                foreach (var quality in group.Qualities)
                {
                    if (!catalog.Contains(quality, StringComparer.OrdinalIgnoreCase) || !grouped.Add(quality)) throw new DataValidationException(nameof(input.QualityGroups), "unknown_catalog_value");
                }
            }

            var groups = input.QualityGroups.Select(group => new QualityGroup(group.Id, group.Name, [.. catalog.Where(quality => group.Qualities.Contains(quality, StringComparer.OrdinalIgnoreCase))])).ToArray();
            var expected = catalog.Select(quality => groups.FirstOrDefault(group => group.Qualities.Contains(quality, StringComparer.OrdinalIgnoreCase))?.Key ?? quality).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var names = expected.ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);
            var order = input.QualityOrder.Length == 0 ? expected : input.QualityOrder.Select(item => names.GetValueOrDefault(item) ?? throw new DataValidationException(nameof(input.QualityOrder), "unknown_catalog_value")).ToArray();
            if (order.Length != expected.Length || order.Distinct(StringComparer.OrdinalIgnoreCase).Count() != expected.Length) throw new DataValidationException(nameof(input.QualityOrder), "invalid_transition");
            var allowed = order.Where(quality => input.AllowedQualities.Contains(quality, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (allowed.Length == 0 || allowed.Length != input.AllowedQualities.Length) throw new DataValidationException(nameof(input.AllowedQualities), "unknown_catalog_value");
            var cutoff = input.UpgradeAllowed ? allowed.FirstOrDefault(quality => string.Equals(quality, input.UpgradeCutoffQuality, StringComparison.OrdinalIgnoreCase)) : null;
            if (input.UpgradeAllowed && cutoff is null) throw new DataValidationException(nameof(input.UpgradeCutoffQuality), "unknown_catalog_value");

            if (input.FormatScores.Length > 1000 || input.FormatScores.Select(score => score.FormatId).Distinct().Count() != input.FormatScores.Length
                || input.FormatScores.Any(score => score.FormatId < 1 || score.Score is < -1000000 or > 1000000)) throw new DataValidationException(nameof(input.FormatScores), "invalid_transition");
            if (input.MinimumUpgradeFormatScore is < 1 or > 1000000 || input.MinimumFormatScore is < -1000000 or > 1000000 || input.CutoffFormatScore is < -1000000 or > 1000000)
                throw new DataValidationException(nameof(input.MinimumFormatScore), "out_of_range");

            // The languages of the choice are known two-letter codes, each once and in the given order.
            var languages = input.LanguageOrder.Select(LanguageCodes.ToTwoLetter).ToArray();
            if (languages.Any(code => code is null))
                throw new DataValidationException(nameof(input.LanguageOrder), "unknown_language");
            var languageOrder = languages.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();

            var stored = existing is null ? null : JsonSerializer.Deserialize<AcquisitionProfileDefinition>(existing, AcquisitionSelectionLogic.DefinitionOptions);
            var definition = new AcquisitionProfileDefinition(allowed, order, input.UpgradeAllowed, cutoff, input.MinimumSizeMb * Megabyte, input.MaximumSizeMb * Megabyte)
            {
                Monitoring = input.Monitoring ?? stored?.Monitoring,
                QualityGroups = groups,
                FormatScores = input.FormatScores,
                MinimumFormatScore = input.MinimumFormatScore,
                CutoffFormatScore = input.CutoffFormatScore,
                MinimumUpgradeFormatScore = input.MinimumUpgradeFormatScore,
                LanguageOrder = languageOrder,
                LanguageFallback = input.LanguageFallback
            };
            return JsonSerializer.Serialize(definition, AcquisitionSelectionLogic.DefinitionOptions);
        }

        public static async Task<long> CreateAcquisitionProfileAsync(this LogicContext ctx, AcquisitionProfileInputData input, CancellationToken ct)
        {
            await ctx.RequireProfileFormatsAsync(input, ct);
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

            await ctx.RequireProfileFormatsAsync(input, ct);
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
            var state = await ctx.ReadAcquisitionProfileStateAsync(data.Id, ct);
            if (state.Version != data.Version) throw new DataValidationException(nameof(data.Version), "outdated_version");
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
                    AND profile."Version" = @Version
                    AND NOT EXISTS (SELECT 1 FROM "WantedItems" AS wanted WHERE wanted."AcquisitionProfileId" = profile."Id")
                    AND NOT EXISTS (SELECT 1 FROM "RequestRules" AS rule WHERE profile."Id" = ANY(rule."AcquisitionProfileIds"))
                    AND NOT EXISTS (SELECT 1 FROM "AccountRequestRules" AS rule WHERE profile."Id" = ANY(rule."AcquisitionProfileIds"))
                    AND NOT EXISTS (SELECT 1 FROM "AcquisitionRequests" AS request WHERE request."AcquisitionProfileId" = profile."Id" AND request."AcquisitionRequestStatusTypeId" BETWEEN 1 AND 5);
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Id), "in_use");
            }

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

        private static async Task RequireProfileFormatsAsync(this LogicContext ctx, AcquisitionProfileInputData input, CancellationToken ct)
        {
            if (input.FormatScores.Length == 0) return;
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    format."Id",
                    format."MediaTypeId"
                FROM
                    "CustomFormats" AS format
                WHERE
                    format."Id" = ANY(@FormatIds)
                ORDER BY
                    format."Id"
                FOR SHARE;
                """;
            sql.Parameters.Add(new ProfileFormatReferencesData(input.FormatScores.Select(score => score.FormatId).ToArray()));
            var formats = await ctx.Sql.ReadListAsync<ProfileFormatScopeData>(sql, ct);
            if (formats.Count != input.FormatScores.Length || formats.Any(format => format.MediaTypeId != input.MediaTypeId)) throw new DataValidationException(nameof(input.FormatScores), "unknown_reference");
        }

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

    }
}
