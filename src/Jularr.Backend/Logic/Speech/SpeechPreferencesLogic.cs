using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Speech;
using Jularr.Backend.Data.Logic.Speech;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Profile;

namespace Jularr.Backend.Logic.Speech
{
    internal static class SpeechPreferencesLogic
    {
        internal static async Task SaveSpeechPreferencesAsync(this LogicContext ctx, SpeechPreferencesData data, SpeechModel? model, CancellationToken ct)
        {
            if (data.ProviderTypeId == SpeechProviderType.Device && data.ModelId is not null
                || data.ProviderTypeId == SpeechProviderType.OfflineNeural && (model is null || data.VoiceId != model.Voice
                    || data.Language.Split('-')[0] != model.Language.Split('-')[0]))
            {
                throw new DataValidationException(nameof(data.ModelId), "unknown_reference");
            }

            // Lock one profile, then its bounded language map; stale settings never overwrite another device.
            await ctx.LockProfileAsync(data.ProfileId, ct);
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ProfileSpeechSettings" ("ProfileId", "Rate", "Pitch", "Volume", "Version")
                SELECT
                    @ProfileId,
                    @Rate,
                    @Pitch,
                    @Volume,
                    1
                WHERE
                    @Version = 0
                    OR EXISTS (SELECT 1 FROM "ProfileSpeechSettings" WHERE "ProfileId" = @ProfileId AND "Version" = @Version)
                ON CONFLICT ("ProfileId") DO UPDATE
                SET
                    "Rate" = @Rate,
                    "Pitch" = @Pitch,
                    "Volume" = @Volume,
                    "Version" = "ProfileSpeechSettings"."Version" + 1
                WHERE
                    "ProfileSpeechSettings"."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1) throw new DataValidationException(nameof(data.Version), "outdated_version");

            if (data.ProviderTypeId == SpeechProviderType.Device && data.VoiceId is null)
            {
                sql = new SqlStatement();
                sql.Statement =
                    """
                    DELETE FROM
                        "ProfileSpeechVoices"
                    WHERE
                        "ProfileId" = @ProfileId
                        AND "Language" = @Language;
                    """;
                sql.Parameters.Add(data);
                await ctx.ExecuteAsyncForAffectedRows(sql, ct);
                return;
            }

            if (model is not null)
            {
                sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        enabled."Hash",
                        enabled."IsEnabled",
                        enabled."Version"
                    FROM
                        "SpeechModels" AS enabled
                    WHERE
                        enabled."Hash" = @Hash
                        AND enabled."IsEnabled" = TRUE
                    FOR SHARE OF
                        enabled;
                    """;
                sql.Parameters.Add(new SpeechModelData(model.Sha256, true, 0));
                _ = await ctx.ReadOptionalAsync<SpeechModelData>(sql, ct) ?? throw new DataValidationException(nameof(data.ModelId), "unknown_reference");
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ProfileSpeechVoices" ("ProfileId", "Language", "ProviderTypeId", "ModelId", "VoiceId")
                SELECT
                    @ProfileId,
                    @Language,
                    @ProviderTypeId,
                    @ModelId,
                    @VoiceId
                WHERE
                    EXISTS (SELECT 1 FROM "ProfileSpeechVoices" WHERE "ProfileId" = @ProfileId AND "Language" = @Language)
                    OR (SELECT count(*) FROM "ProfileSpeechVoices" WHERE "ProfileId" = @ProfileId) < 32
                ON CONFLICT ("ProfileId", "Language") DO UPDATE
                SET
                    "ProviderTypeId" = @ProviderTypeId,
                    "ModelId" = @ModelId,
                    "VoiceId" = @VoiceId;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1) throw new DataValidationException(nameof(data.Language), "out_of_range");
        }
    }
}
