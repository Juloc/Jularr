using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class CustomFormatLogic
    {
        // A format belongs to every media type: each condition must fit one type that has profiles (a source takes the names of Radarr or Sonarr). An import names the type it comes from.
        public static void ValidateConditions(MediaType? mediaType, IReadOnlyList<FormatCondition> conditions)
        {
            MediaType[] types = mediaType is { } single ? [single] : [MediaType.Movie, MediaType.Series, MediaType.Book, MediaType.Music, MediaType.Game];
            if (conditions.Count is < 1 or > 64) throw new DataValidationException("Conditions", "out_of_range");
            for (var index = 0; index < conditions.Count; index++)
            {
                var condition = conditions[index];
                condition.Validate();
                var supported = types.Where(type => FormatConditionKinds.Supports(type, condition.Kind)).ToArray();
                if (supported.Length == 0) throw new DataValidationException("Conditions", "invalid_type");
                if (!supported.Any(type => Fits(type, condition))) throw new DataValidationException("Conditions[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]."
                    + (condition.Kind == FormatConditionKind.Resolution ? nameof(condition.Resolution)
                        : condition.Kind is FormatConditionKind.Size or FormatConditionKind.Year ? nameof(condition.Minimum) : nameof(condition.Value)), "invalid_transition");
            }
        }

        private static bool Fits(MediaType mediaType, FormatCondition condition) => condition.Kind switch
        {
            FormatConditionKind.ReleaseTitle or FormatConditionKind.ReleaseGroup => ValidPattern(condition.Value),
            FormatConditionKind.Edition => mediaType == MediaType.Movie && ValidPattern(condition.Value),
            FormatConditionKind.Language => condition.Value is "original" or "any" or "unknown" || condition.Value is { Length: 2 } code && code.All(char.IsAsciiLetterLower),
            FormatConditionKind.Source => mediaType switch
            {
                MediaType.Movie => new[] { "unknown", "cam", "telesync", "telecine", "workprint", "dvd", "tv", "webdl", "webrip", "bluray" }.Contains(condition.Value, StringComparer.OrdinalIgnoreCase),
                MediaType.Series => new[] { "unknown", "television", "televisionRaw", "web", "webRip", "dvd", "bluray", "blurayRaw" }.Contains(condition.Value, StringComparer.OrdinalIgnoreCase),
                _ => false
            },
            FormatConditionKind.Resolution => mediaType is MediaType.Movie or MediaType.Series && condition.Resolution is 0 or 360 or 480 or 540 or 576 or 720 or 1080 or 2160 or 4320,
            FormatConditionKind.QualityModifier => condition.Value is "none" or "remux" or "brdisk" or "rawhd" or "regional" or "screener",
            FormatConditionKind.IndexerFlag => condition.Value is { Length: > 0 and <= 128 } flag && flag.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or ' '),
            FormatConditionKind.Size or FormatConditionKind.Year => (condition.Minimum is not null || condition.Maximum is not null)
                && (condition.Minimum is not { } minimum || minimum >= 0) && (condition.Maximum is not { } maximum || maximum > 0)
                && (condition.Minimum is not { } lower || condition.Maximum is not { } upper || lower < upper),
            FormatConditionKind.ReleaseType => mediaType == MediaType.Series && condition.Value is "unknown" or "singleEpisode" or "multiEpisode" or "seasonPack",
            _ => false
        };

        private static bool ValidPattern(string? pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 2000) return false;
            try
            {
                _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public static async Task<CustomFormatKeyData> SaveCustomFormatAsync(this LogicContext ctx, CustomFormatWriteData data, CancellationToken ct)
        {
            var conditions = JsonSerializer.Deserialize<FormatCondition[]>(data.Conditions, AcquisitionSelectionLogic.DefinitionOptions)
                ?? throw new DataValidationException("Conditions", "required");
            ValidateConditions(null, conditions);
            if (data.Id is not null && data.Version is null) throw new DataValidationException(nameof(data.Version), "required");

            // One name for every type, in any case.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    EXISTS (SELECT 1 FROM "CustomFormats" AS other WHERE lower(other."Name") = lower(@Name) AND other."Id" IS DISTINCT FROM @Id) AS "IsInUse";
                """;
            sql.Parameters.Add(data);
            if ((await ctx.ReadRequiredAsync<AcquisitionProfileUseData>(sql, ct)).IsInUse) throw new DataValidationException(nameof(data.Name), "duplicate_value");

            sql = new SqlStatement();
            if (data.Id is null)
            {
                sql.Statement =
                    """
                    INSERT INTO
                        "CustomFormats" ("Name", "IncludeWhenRenaming", "Conditions")
                    VALUES
                        (@Name, @IncludeWhenRenaming, @Conditions::jsonb)
                    ON CONFLICT ((lower("Name"))) DO NOTHING
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
                        "CustomFormats" AS format
                    SET
                        "Name" = @Name,
                        "IncludeWhenRenaming" = @IncludeWhenRenaming,
                        "Conditions" = @Conditions::jsonb,
                        "Version" = format."Version" + 1
                    WHERE
                        format."Id" = @Id
                        AND format."Version" = @Version
                    RETURNING
                        format."Id",
                        format."Version";
                    """;
            }

            sql.Parameters.Add(data);
            return await ctx.ReadOptionalAsync<CustomFormatKeyData>(sql, ct) ?? throw new DataValidationException(nameof(data.Version), "outdated_version");
        }

        public static async Task DeleteCustomFormatAsync(this LogicContext ctx, CustomFormatKeyData data, CancellationToken ct)
        {
            // Profile saves share-lock referenced formats, so no new reference can race deletion.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    format."Id",
                    format."Version"
                FROM
                    "CustomFormats" AS format
                WHERE
                    format."Id" = @Id
                FOR UPDATE;
                """;
            sql.Parameters.Add(data);
            var current = await ctx.ReadOptionalAsync<CustomFormatKeyData>(sql, ct);
            if (current?.Version != data.Version) throw new DataValidationException(nameof(data.Version), "outdated_version");

            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "CustomFormats" AS format
                WHERE
                    format."Id" = @Id
                    AND format."Version" = @Version
                    AND NOT EXISTS (
                        SELECT 1
                        FROM "AcquisitionProfiles" AS profile,
                            jsonb_array_elements(coalesce(profile."Definition" -> 'FormatScores', '[]'::jsonb)) AS score
                        WHERE (score ->> 'FormatId')::bigint = format."Id"
                    );
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1) throw new DataValidationException(nameof(data.Id), "in_use");
        }
    }
}
