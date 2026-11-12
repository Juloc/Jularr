using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class AcquisitionProfileLogic
    {
        private const long Megabyte = 1024L * 1024L;

        // A profile applies to at least one media type, all of one quality list, and is the default only of types it applies to.
        public static QualityFamily FamilyOf(AcquisitionProfileInputData input)
        {
            if (input.MediaKinds.Length == 0) throw new DataValidationException(nameof(input.MediaKinds), "required");
            var family = AcquisitionMediaKinds.Family(input.MediaKinds[0]);
            if (input.MediaKinds.Any(kind => AcquisitionMediaKinds.Family(kind) != family)) throw new DataValidationException(nameof(input.MediaKinds), "mixed_quality_lists");
            if (input.DefaultFor.Any(kind => !input.MediaKinds.Contains(kind))) throw new DataValidationException(nameof(input.DefaultFor), "invalid_transition");
            return family;
        }

        public static string BuildDefinition(AcquisitionProfileInputData input, string? existing)
        {
            input.Validate();
            var family = FamilyOf(input);
            if (input.MinimumSizeMb is { } minimum && input.MaximumSizeMb is { } maximum && minimum > maximum) throw new DataValidationException(nameof(input.MaximumSizeMb), "out_of_range");

            var catalog = ProfileQualities.For(family);
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
            if (input.GoodGroupScore is < 0 or > 1000000) throw new DataValidationException(nameof(input.GoodGroupScore), "out_of_range");
            if (input.BadGroupScore is < -1000000 or > 0) throw new DataValidationException(nameof(input.BadGroupScore), "out_of_range");
            if (!Enum.IsDefined(input.CodecPreference) || !Enum.IsDefined(input.SizePreference)) throw new DataValidationException(nameof(input.CodecPreference), "out_of_range");

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
                LanguageFallback = input.LanguageFallback,
                GoodGroupScore = input.GoodGroupScore,
                BadGroupScore = input.BadGroupScore,
                BlockBadSources = input.BlockBadSources,
                CodecPreference = input.CodecPreference,
                SizePreference = input.SizePreference
            };
            return JsonSerializer.Serialize(definition, AcquisitionSelectionLogic.DefinitionOptions);
        }

        public static async Task<long> CreateAcquisitionProfileAsync(this LogicContext ctx, AcquisitionProfileInputData input, CancellationToken ct)
        {
            await ctx.RequireProfileFormatsAsync(input, ct);
            var row = new AcquisitionProfileRowData(null, input.Name, BuildDefinition(input, null), null);
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AcquisitionProfiles" ("Name", "Definition")
                VALUES
                    (@Name, @Definition::jsonb)
                RETURNING
                    "Id",
                    "Version";
                """;
            sql.Parameters.Add(row);
            var id = (await ctx.ReadRequiredAsync<AcquisitionProfileKeyData>(sql, ct)).Id;
            await ctx.SaveProfileKindsAsync(new AcquisitionProfileKindsData(id, input.MediaKinds, input.DefaultFor), ct);
            return id;
        }

        public static async Task UpdateAcquisitionProfileAsync(this LogicContext ctx, AcquisitionProfileInputData input, CancellationToken ct)
        {
            // A default is replaced by another profile, never removed; a type stays while targets of that type chose the profile.
            var state = await ctx.ReadAcquisitionProfileStateAsync(input.Id!.Value, ct);
            if (state.Version != input.Version)
            {
                throw new DataValidationException(nameof(input.Version), "outdated_version");
            }

            if (state.DefaultFor.Any(kind => !input.DefaultFor.Contains(kind)))
            {
                throw new DataValidationException(nameof(input.DefaultFor), "default_required");
            }

            var kinds = new AcquisitionProfileKindsData(state.Id, input.MediaKinds, input.DefaultFor);
            if (state.MediaKinds.Any(kind => !input.MediaKinds.Contains(kind)))
            {
                var used = new SqlStatement();
                used.Statement =
                    """
                    SELECT
                        EXISTS (
                            SELECT
                                1
                            FROM
                                "WantedItems" AS wanted
                                JOIN "WorkAcquisitionKinds" AS kind ON kind."WorkId" = wanted."WorkId"
                            WHERE
                                wanted."AcquisitionProfileId" = @Id
                                AND NOT (kind."AcquisitionMediaKindId" = ANY(@MediaKinds))
                        ) AS "IsInUse";
                    """;
                used.Parameters.Add(kinds);
                if ((await ctx.ReadRequiredAsync<AcquisitionProfileUseData>(used, ct)).IsInUse) throw new DataValidationException(nameof(input.MediaKinds), "kind_in_use");
            }

            await ctx.RequireProfileFormatsAsync(input, ct);
            var row = new AcquisitionProfileRowData(state.Id, input.Name, BuildDefinition(input, state.Definition), state.Version);
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AcquisitionProfiles" AS profile
                SET
                    "Name" = @Name,
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

            await ctx.SaveProfileKindsAsync(kinds, ct);
        }

        public static async Task DeleteAcquisitionProfileAsync(this LogicContext ctx, AcquisitionProfileKeyData data, CancellationToken ct)
        {
            var state = await ctx.ReadAcquisitionProfileStateAsync(data.Id, ct);
            if (state.Version != data.Version) throw new DataValidationException(nameof(data.Version), "outdated_version");
            if (state.DefaultFor.Length > 0)
            {
                throw new DataValidationException(nameof(AcquisitionProfileInputData.DefaultFor), "default_required");
            }

            // Titles and open requests that chose the profile keep it; the request rules that offer it lose it with the profile.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    EXISTS (SELECT 1 FROM "WantedItems" AS wanted WHERE wanted."AcquisitionProfileId" = @Id) AS "HasTitles",
                    EXISTS (SELECT 1 FROM "AcquisitionRequests" AS request
                        WHERE request."AcquisitionProfileId" = @Id AND request."AcquisitionRequestStatusTypeId" BETWEEN 1 AND 5) AS "HasRequests";
                """;
            sql.Parameters.Add(data);
            var use = await ctx.ReadRequiredAsync<AcquisitionProfileBlockersData>(sql, ct);
            if (use.HasTitles) throw new DataValidationException(nameof(data.Id), "profile_has_titles");
            if (use.HasRequests) throw new DataValidationException(nameof(data.Id), "profile_has_requests");

            sql = new SqlStatement();
            sql.Statement =
                """
                WITH rules AS (
                    UPDATE "RequestRules" AS rule
                    SET "AcquisitionProfileIds" = array_remove(rule."AcquisitionProfileIds", @Id), "Version" = rule."Version" + 1
                    WHERE @Id = ANY(rule."AcquisitionProfileIds")
                ),
                accounts AS (
                    UPDATE "AccountRequestRules" AS rule
                    SET "AcquisitionProfileIds" = array_remove(rule."AcquisitionProfileIds", @Id)
                    WHERE @Id = ANY(rule."AcquisitionProfileIds")
                )
                DELETE FROM
                    "AcquisitionProfiles" AS profile
                WHERE
                    profile."Id" = @Id
                    AND profile."Version" = @Version
                    AND NOT EXISTS (SELECT 1 FROM "WantedItems" AS wanted WHERE wanted."AcquisitionProfileId" = profile."Id")
                    AND NOT EXISTS (SELECT 1 FROM "AcquisitionRequests" AS request WHERE request."AcquisitionProfileId" = profile."Id" AND request."AcquisitionRequestStatusTypeId" BETWEEN 1 AND 5);
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Id), "in_use");
            }
        }

        private static async Task SaveProfileKindsAsync(this LogicContext ctx, AcquisitionProfileKindsData data, CancellationToken ct)
        {
            // The types it applies to, then the types it becomes the default of: one default per type, the earlier one steps back.
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "AcquisitionProfileMediaKinds" AS applies
                WHERE
                    applies."AcquisitionProfileId" = @Id
                    AND NOT (applies."AcquisitionMediaKindId" = ANY(@MediaKinds));
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AcquisitionProfileMediaKinds" ("AcquisitionProfileId", "AcquisitionMediaKindId")
                SELECT
                    @Id,
                    kind
                FROM
                    unnest(@MediaKinds) AS kind
                ON CONFLICT DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            if (data.DefaultFor.Length == 0) return;

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AcquisitionProfileDefaults" ("AcquisitionMediaKindId", "AcquisitionProfileId")
                SELECT
                    kind,
                    @Id
                FROM
                    unnest(@DefaultFor) AS kind
                ON CONFLICT ("AcquisitionMediaKindId") DO UPDATE SET
                    "AcquisitionProfileId" = EXCLUDED."AcquisitionProfileId";
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        private static async Task RequireProfileFormatsAsync(this LogicContext ctx, AcquisitionProfileInputData input, CancellationToken ct)
        {
            // Formats belong to every type; profile saves share-lock them, so deleting one cannot race a new score.
            if (input.FormatScores.Length == 0) return;
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    format."Id",
                    format."Version"
                FROM
                    "CustomFormats" AS format
                WHERE
                    format."Id" = ANY(@FormatIds)
                ORDER BY
                    format."Id"
                FOR SHARE;
                """;
            sql.Parameters.Add(new ProfileFormatReferencesData(input.FormatScores.Select(score => score.FormatId).ToArray()));
            var formats = await ctx.Sql.ReadListAsync<CustomFormatKeyData>(sql, ct);
            if (formats.Count != input.FormatScores.Length) throw new DataValidationException(nameof(input.FormatScores), "unknown_reference");
        }

        private static Task<AcquisitionProfileStateData> ReadAcquisitionProfileStateAsync(this LogicContext ctx, long id, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    coalesce((
                        SELECT array_agg(applies."AcquisitionMediaKindId" ORDER BY applies."AcquisitionMediaKindId")
                        FROM "AcquisitionProfileMediaKinds" AS applies
                        WHERE applies."AcquisitionProfileId" = profile."Id"
                    ), '{}') AS "MediaKinds",
                    coalesce((
                        SELECT array_agg(standard."AcquisitionMediaKindId" ORDER BY standard."AcquisitionMediaKindId")
                        FROM "AcquisitionProfileDefaults" AS standard
                        WHERE standard."AcquisitionProfileId" = profile."Id"
                    ), '{}') AS "DefaultFor",
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
