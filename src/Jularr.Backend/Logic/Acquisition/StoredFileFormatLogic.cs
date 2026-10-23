using System.Text.Json;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class StoredFileFormatLogic
    {
        // A small bounded pass. Changed conditions or file/work evidence invalidate matches through version/evidence equality, without rescanning files.
        public static async Task<int> RefreshStoredFileFormatsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var languageCount = await RefreshLanguagesAsync(ctx, ct);
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT evidence."StoredFileId", format."Id" AS "CustomFormatId", format."Version" AS "FormatVersion",
                    format."Name", format."Conditions"::text AS "Conditions", evidence."Evidence"::text AS "Evidence"
                FROM "StoredFileFormatEvidence" AS evidence
                    JOIN "CustomFormats" AS format ON format."MediaTypeId" = evidence."MediaTypeId"
                    LEFT JOIN "StoredFileCustomFormats" AS evaluated ON evaluated."StoredFileId" = evidence."StoredFileId" AND evaluated."CustomFormatId" = format."Id"
                WHERE EXISTS (SELECT 1 FROM "WantedItems" AS wanted WHERE wanted."WorkId" = evidence."WorkId")
                    AND EXISTS (
                        SELECT 1 FROM "AcquisitionProfiles" AS profile
                            CROSS JOIN LATERAL jsonb_array_elements(coalesce(profile."Definition" -> 'FormatScores', '[]'::jsonb)) AS score
                        WHERE profile."MediaTypeId" = evidence."MediaTypeId" AND (score ->> 'FormatId')::bigint = format."Id"
                    )
                    AND (evaluated."FormatVersion" IS DISTINCT FROM format."Version"
                        OR evaluated."Evidence" IS DISTINCT FROM evidence."Evidence")
                ORDER BY evidence."StoredFileId", format."Id"
                LIMIT 100;
                """;
            var pending = await ctx.ReadSql.ReadListAsync<StoredFileFormatEvaluationData>(sql, ct);
            if (pending.Count == 0) return languageCount;
            var matches = pending.Select(row =>
            {
                var format = new CustomFormat(row.CustomFormatId, row.Name, false,
                    JsonSerializer.Deserialize<FormatCondition[]>(row.Conditions, AcquisitionSelectionLogic.DefinitionOptions)!);
                var facts = JsonSerializer.Deserialize<StoredReleaseEvidence>(row.Evidence)!;
                var candidate = Candidate(facts);
                var matches = false;
                var valid = candidate is null || CustomFormatMatcher.TryMatch(format, candidate, new FormatContext(facts.Movie, facts.OriginalLanguage, facts.Year), out matches);
                return new StoredFileFormatMatch(row.StoredFileId, row.CustomFormatId, row.FormatVersion, row.Evidence, matches, valid);
            }).ToArray();
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO "StoredFileCustomFormats" ("StoredFileId", "CustomFormatId", "FormatVersion", "Evidence", "Matches", "IsValid")
                SELECT row."StoredFileId", row."CustomFormatId", row."FormatVersion", row."Evidence"::jsonb, row."Matches", row."IsValid"
                FROM jsonb_to_recordset(@Rows::jsonb) AS row("StoredFileId" bigint, "CustomFormatId" bigint, "FormatVersion" bigint, "Evidence" text, "Matches" boolean, "IsValid" boolean)
                    JOIN "StoredFiles" AS file ON file."Id" = row."StoredFileId"
                    JOIN "CustomFormats" AS format ON format."Id" = row."CustomFormatId" AND format."Version" = row."FormatVersion"
                ON CONFLICT ("StoredFileId", "CustomFormatId") DO UPDATE
                SET "FormatVersion" = EXCLUDED."FormatVersion", "Evidence" = EXCLUDED."Evidence", "Matches" = EXCLUDED."Matches", "IsValid" = EXCLUDED."IsValid";
                """;
            sql.Parameters.Add(new StoredFileEvidenceBatchData(JsonSerializer.Serialize(matches)));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            return languageCount + pending.Count;
        }

        private static async Task<int> RefreshLanguagesAsync(LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT evidence."StoredFileId", evidence."Evidence"::text AS "Evidence"
                FROM "StoredFileFormatEvidence" AS evidence
                    JOIN "StoredFiles" AS file ON file."Id" = evidence."StoredFileId"
                WHERE evidence."MediaTypeId" = 3
                    AND file."ReleaseLanguageEvidence" IS DISTINCT FROM evidence."Evidence"
                    AND EXISTS (SELECT 1 FROM "WantedItems" AS wanted WHERE wanted."WorkId" = evidence."WorkId")
                ORDER BY evidence."StoredFileId"
                LIMIT 100;
                """;
            var pending = await ctx.ReadSql.ReadListAsync<StoredFileLanguageEvidenceData>(sql, ct);
            if (pending.Count == 0) return 0;
            var rows = pending.Select(row =>
            {
                var facts = JsonSerializer.Deserialize<StoredReleaseEvidence>(row.Evidence)!;
                return new { row.StoredFileId, row.Evidence, Languages = ReleaseWords.ReadLanguages(facts.ReleaseName, facts.OriginalLanguage?.ToUpperInvariant() ?? "EN")
                    .Select(language => language.ToLowerInvariant()).ToArray() };
            }).ToArray();
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE "StoredFiles" AS file
                SET "ReleaseLanguages" = row."Languages", "ReleaseLanguageEvidence" = row."Evidence"::jsonb
                FROM jsonb_to_recordset(@Rows::jsonb) AS row("StoredFileId" bigint, "Evidence" text, "Languages" varchar[])
                    JOIN "StoredFileFormatEvidence" AS evidence ON evidence."StoredFileId" = row."StoredFileId" AND evidence."Evidence" = row."Evidence"::jsonb
                WHERE file."Id" = row."StoredFileId";
                """;
            sql.Parameters.Add(new StoredFileEvidenceBatchData(JsonSerializer.Serialize(rows)));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            return pending.Count;
        }

        public static ReleaseCandidate? Candidate(StoredReleaseEvidence facts)
        {
            var release = facts.MediaTypeId == MediaType.Book ? BookReleaseJudge.Parse(facts.ReleaseName)
                : ReleaseParser.TryParse(facts.ReleaseName, out var parsed) ? parsed : null;
            return release is not null ? new ReleaseCandidate(release, facts.SizeBytes)
                {
                    IndexerFlags = facts.IndexerFlags,
                    Languages = facts.MediaTypeId == MediaType.Book ? ReleaseWords.ReadLanguages(facts.ReleaseName, facts.OriginalLanguage?.ToUpperInvariant() ?? "EN") : null
                } : null;
        }
    }
}
