using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.User.Playback.V1
{
    // The subtitle a profile may read: a subtitle file of the asset or a text stream of its played file.
    internal static class PlaybackSubtitleSources
    {
        // The cache name of a translation of this subtitle: the file as it was probed, the stream and the target language.
        public static string CacheKey(PlaybackSubtitleSourceData source, int? streamIndex, string language) =>
            source.StoredFileId.ToString(global::System.Globalization.CultureInfo.InvariantCulture) + "-" + (streamIndex is { } stream ? PlaybackTracks.StreamKey(stream) : "file") + "-"
            + source.ModifiedAt.Ticks.ToString(global::System.Globalization.CultureInfo.InvariantCulture) + "-" + language;

        // The subtitle as WebVTT, or as ASS from a track that is ASS or SSA and asked for as it is.
        public static async Task<string> ReadAsync(LogicContext context, StorageAccess storage, PlaybackSubtitles subtitles, long mediaAssetId, long profileId, string key, bool styled, CancellationToken ct)
        {
            var (source, streamIndex) = await FindAsync(sql => context.ReadSql.ReadOptionalAsync<PlaybackSubtitleSourceData>(sql, ct), mediaAssetId, profileId, key);
            if (styled && !(streamIndex is not null ? PlaybackTracks.IsStyled(source.Codec) : PlaybackTracks.IsStyledFile(source.RelativePath)))
            {
                throw new KeyNotFoundException("The subtitle was not found.");
            }

            var files = storage.Open(await context.ReadStorageMountAsync(source.StorageMountId, ct));
            var path = StoragePath.Combine(source.RootRelativePath, source.RelativePath);
            var cacheKey = source.StoredFileId.ToString(global::System.Globalization.CultureInfo.InvariantCulture) + "-" + (streamIndex is { } stream ? PlaybackTracks.StreamKey(stream) : "file") + "-"
                + source.ModifiedAt.Ticks.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
            return await subtitles.ReadAsync(files, path, streamIndex, cacheKey, styled, ct);
        }

        public static async Task<(PlaybackSubtitleSourceData Source, int? StreamIndex)> FindAsync(Func<SqlStatement, Task<PlaybackSubtitleSourceData?>> read, long mediaAssetId, long profileId, string key)
        {
            // A subtitle file of the asset, or a text stream of its played file.
            var (streamIndex, fileId) = PlaybackTracks.ParseKey(key);
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."Id" AS "StoredFileId",
                    file."RelativePath",
                    file."ModifiedAt",
                    root."RelativePath" AS "RootRelativePath",
                    mount."Id" AS "StorageMountId",
                    track."Codec"
                FROM
                    "MediaAssets" AS asset
                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                    LEFT JOIN "MediaTracks" AS track ON track."StoredFileId" = file."Id" AND track."StreamIndex" = @StreamIndex AND track."MediaTrackTypeId" = 3
                WHERE
                    asset."Id" = @MediaAssetId
                    AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId)
                    AND file."MissingSince" IS NULL
                    AND (file."Id" = @StoredFileId AND file."RelativePath" ~* '\.(srt|vtt|ass|ssa)$'
                        OR @StoredFileId::bigint IS NULL AND track."StreamIndex" IS NOT NULL AND file."Id" = (
                            SELECT
                                media."Id"
                            FROM
                                "StoredFiles" AS media
                            WHERE
                                media."MediaAssetId" = asset."Id"
                                AND media."MissingSince" IS NULL
                                AND NOT media."IsSubtitle"
                            ORDER BY
                                media."Id"
                            LIMIT 1
                        ));
                """;
            sql.Parameters.Add(new PlaybackSubtitleLookupData(mediaAssetId, profileId, streamIndex, fileId));
            var source = await read(sql) ?? throw new KeyNotFoundException("The subtitle was not found.");
            if (streamIndex is not null && !PlaybackTracks.IsText(source.Codec))
            {
                throw new KeyNotFoundException("The subtitle was not found.");
            }

            return (source, streamIndex);
        }
    }
}
