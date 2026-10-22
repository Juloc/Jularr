using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Profile;

namespace Jularr.Backend.Logic.Reading
{
    internal static class ReaderSettingsLogic
    {
        public static async Task SaveReaderSettingsAsync(this LogicContext ctx, ReaderSettingsSaveData data, CancellationToken ct)
        {
            // One save per profile at a time.
            await ctx.LockProfileAsync(data.ProfileId, ct);

            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ProfileReaderSettings" ("ProfileId", "FontTypeId", "Size", "LineSpacing", "ParagraphSpacing", "TextWidth", "Hyphenation")
                VALUES
                    (@ProfileId, @FontTypeId, @Size, @LineSpacing, @ParagraphSpacing, @TextWidth, @Hyphenation)
                ON CONFLICT ("ProfileId") DO UPDATE
                SET
                    "FontTypeId" = EXCLUDED."FontTypeId",
                    "Size" = EXCLUDED."Size",
                    "LineSpacing" = EXCLUDED."LineSpacing",
                    "ParagraphSpacing" = EXCLUDED."ParagraphSpacing",
                    "TextWidth" = EXCLUDED."TextWidth",
                    "Hyphenation" = EXCLUDED."Hyphenation",
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
