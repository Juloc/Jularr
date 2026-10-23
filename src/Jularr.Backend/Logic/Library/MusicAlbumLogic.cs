using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Library
{
    // Unassigned files of a library and the albums their tags name.
    internal static class MusicAlbumLogic
    {
        public static Task<IReadOnlyList<StoredFileCandidateData>> ReadUnassignedFilesAsync(this LogicContext ctx, StoredFileCursorData cursor, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."Id",
                    file."RelativePath",
                    file."SizeBytes",
                    file."ModifiedAt",
                    NULL::bigint AS "WorkId"
                FROM
                    "StoredFiles" AS file
                WHERE
                    file."LibraryRootId" = @LibraryRootId
                    AND file."Id" > @AfterId
                    AND file."MediaAssetId" IS NULL
                    AND file."MissingSince" IS NULL
                    AND NOT file."IsSubtitle"
                ORDER BY
                    file."Id"
                LIMIT 100;
                """;
            sql.Parameters.Add(cursor);
            return ctx.ReadSql.ReadListAsync<StoredFileCandidateData>(sql, ct);
        }

        // The albums Jularr already has, with their number of tracks.
        public static Task<IReadOnlyList<MusicAlbumMatchData>> ReadMusicAlbumsAsync(this LogicContext ctx, MusicAlbumLookupData lookup, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    identity."Value" AS "ExternalId",
                    identity."WorkId",
                    (
                        SELECT
                            count(*)::integer
                        FROM
                            "WorkUnits" AS unit
                        WHERE
                            unit."WorkId" = identity."WorkId"
                            AND unit."WorkUnitTypeId" = 5
                    ) AS "TrackCount"
                FROM
                    "WorkExternalIdentities" AS identity
                    JOIN "Works" AS work ON work."Id" = identity."WorkId"
                WHERE
                    identity."Namespace" = 'musicbrainz'
                    AND identity."Value" = ANY(@ExternalIds)
                    AND work."MediaTypeId" = 6;
                """;
            sql.Parameters.Add(lookup);
            return ctx.ReadSql.ReadListAsync<MusicAlbumMatchData>(sql, ct);
        }
    }
}
