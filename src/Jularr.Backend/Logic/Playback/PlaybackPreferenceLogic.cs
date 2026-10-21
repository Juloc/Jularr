using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Profile;

namespace Jularr.Backend.Logic.Playback
{
    internal static class PlaybackPreferenceLogic
    {
        public static async Task SavePlaybackPreferencesAsync(this LogicContext ctx, PlaybackPreferenceData data, CancellationToken ct)
        {
            // One save per profile at a time.
            await ctx.LockProfileAsync(data.ProfileId, ct);

            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ProfilePlaybackPreferences" ("ProfileId", "AudioLanguage", "SubtitleLanguage", "SubtitleModeTypeId", "Speed", "AutoplayNext")
                VALUES
                    (@ProfileId, @AudioLanguage, @SubtitleLanguage, @SubtitleModeTypeId, @Speed, @AutoplayNext)
                ON CONFLICT ("ProfileId") DO UPDATE
                SET
                    "AudioLanguage" = EXCLUDED."AudioLanguage",
                    "SubtitleLanguage" = EXCLUDED."SubtitleLanguage",
                    "SubtitleModeTypeId" = EXCLUDED."SubtitleModeTypeId",
                    "Speed" = EXCLUDED."Speed",
                    "AutoplayNext" = EXCLUDED."AutoplayNext",
                    "UpdatedAt" = now();
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The profile was not found.");
            }
        }

        // The speed chosen in the player; the other choices of a profile without a row stay at their defaults.
        public static async Task SavePlaybackSpeedAsync(this LogicContext ctx, PlaybackSpeedData data, CancellationToken ct)
        {
            await ctx.LockProfileAsync(data.ProfileId, ct);

            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ProfilePlaybackPreferences" ("ProfileId", "Speed")
                VALUES
                    (@ProfileId, @Speed)
                ON CONFLICT ("ProfileId") DO UPDATE
                SET
                    "Speed" = EXCLUDED."Speed",
                    "UpdatedAt" = now();
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The profile was not found.");
            }
        }
    }
}
