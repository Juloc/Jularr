using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Library
{
    internal static class NamingPatternLogic
    {
        public static async Task<NamingSet> ReadNamingPatternsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    pattern."MediaTypeId",
                    pattern."MediaSubtypeId",
                    pattern."Pattern",
                    pattern."DailyFile"
                FROM
                    "NamingPatterns" AS pattern;
                """;
            var stored = await ctx.ReadSql.ReadListAsync<NamingPatternData>(sql, ct);
            var patterns = stored.ToDictionary(pattern => (pattern.MediaTypeId, pattern.MediaSubtypeId), pattern => pattern.Pattern);
            var daily = stored.Where(pattern => pattern.DailyFile is not null).ToDictionary(pattern => (pattern.MediaTypeId, pattern.MediaSubtypeId), pattern => pattern.DailyFile!);
            var rules = (await ctx.ReadNamingSettingsAsync(ct)).ToDictionary(settings => settings.MediaTypeId,
                settings => new NamingRules(settings.ReplacesIllegalCharacters, settings.ColonReplacementId, settings.CustomColonReplacement, settings.SpecialsFolder, settings.RenamesFiles, settings.MultiEpisodeStyleId));
            return new NamingSet(patterns, rules, daily);
        }

        public static async Task<IReadOnlyList<NamingSettingsData>> ReadNamingSettingsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."MediaTypeId",
                    settings."ReplacesIllegalCharacters",
                    settings."ColonReplacementId",
                    settings."CustomColonReplacement",
                    settings."SpecialsFolder",
                    settings."RenamesFiles",
                    settings."MultiEpisodeStyleId",
                    settings."Version"
                FROM
                    "NamingSettings" AS settings
                ORDER BY
                    settings."MediaTypeId";
                """;
            return await ctx.ReadSql.ReadListAsync<NamingSettingsData>(sql, ct);
        }

        public static async Task SaveNamingSettingsAsync(this LogicContext ctx, NamingSettingsData data, CancellationToken ct)
        {
            NamingPattern.ValidateRules(new NamingRules(data.ReplacesIllegalCharacters, data.ColonReplacementId, data.CustomColonReplacement, data.SpecialsFolder));
            if (data.SpecialsFolder is not null && data.MediaTypeId != MediaType.Series)
                throw new DataValidationException(nameof(data.SpecialsFolder), "invalid_value");

            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "NamingSettings" AS settings
                SET
                    "ReplacesIllegalCharacters" = @ReplacesIllegalCharacters,
                    "ColonReplacementId" = @ColonReplacementId,
                    "CustomColonReplacement" = CASE WHEN @ColonReplacementId = 6 THEN @CustomColonReplacement END,
                    "SpecialsFolder" = @SpecialsFolder,
                    "RenamesFiles" = @RenamesFiles,
                    "MultiEpisodeStyleId" = @MultiEpisodeStyleId,
                    "Version" = settings."Version" + 1
                WHERE
                    settings."MediaTypeId" = @MediaTypeId
                    AND settings."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "invalid_transition");
            }
        }

        public static async Task SaveNamingPatternAsync(this LogicContext ctx, NamingPatternSaveData data, CancellationToken ct)
        {
            // Folders and file are stored as one pattern of at most 500 characters.
            NamingPattern.ValidateDaily(data.MediaTypeId, data.MediaSubtypeId, NamingPattern.Split(data.Pattern).Folder, data.DailyFile, nameof(data.DailyFile));

            if (data.Pattern.Length > 500)
            {
                throw new DataValidationException(nameof(data.Pattern), "too_long");
            }

            NamingPattern.Validate(data.MediaTypeId, data.Pattern, nameof(data.Pattern));
            if (data.MediaTypeId is MediaType.Movie or MediaType.Series && (!NamingPattern.Distinguishes(data.Pattern) || data.DailyFile is { } dailyFile && !NamingPattern.Distinguishes(dailyFile)) && (await ctx.ReadReplacementSettingsAsync(ct)).KeepsReleases)
            {
                throw new DataValidationException(nameof(data.Pattern), "naming_needs_release_token");
            }

            // Reject concurrent edits.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "NamingPatterns" AS pattern
                SET
                    "Pattern" = @Pattern,
                    "DailyFile" = @DailyFile,
                    "Version" = pattern."Version" + 1
                WHERE
                    pattern."MediaTypeId" = @MediaTypeId
                    AND pattern."MediaSubtypeId" IS NOT DISTINCT FROM @MediaSubtypeId::smallint
                    AND pattern."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "invalid_transition");
            }
        }
    }
}
