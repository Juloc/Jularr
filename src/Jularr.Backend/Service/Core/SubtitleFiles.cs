using System.Text;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Logic.Storage;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Subtitles;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Core
{
    // The subtitles of one video file for the automatic fetch and the search by hand: what it has, what is asked and the file written beside it.
    internal sealed class SubtitleFiles(StorageAccess storage)
    {
        // Languages the file already has: embedded streams and files beside it, forced ones excepted.
        public static HashSet<string> Present(SubtitleSourceData source) => source.TrackLanguages.Select(LanguageCodes.ToTwoLetter)
            .Concat(source.SiblingPaths.Where(LibraryFileTypes.IsSubtitle).Select(PlaybackTracks.Describe).Where(sibling => !sibling.IsForced).Select(sibling => LanguageCodes.ToTwoLetter(sibling.Language)))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);

        public static SubtitleQuery Query(SubtitleSourceData source, IEnumerable<string> languages) =>
            new(source.MediaTypeId == MediaType.Series, source.ImdbId, source.TmdbId, (int?)source.ParentUnitNumber, (int?)source.UnitNumber, [.. languages]);

        // A subtitle file has timed cues, whatever its encoding.
        public static bool IsSubtitle(byte[] bytes) => Encoding.UTF8.GetString(bytes).Contains("-->", StringComparison.Ordinal) || PlaybackTracks.Decode(bytes).Contains("-->", StringComparison.Ordinal);

        public static string FileName(SubtitleSourceData source, string language) => source.RelativePath[..^Path.GetExtension(source.RelativePath).Length] + "." + language + ".srt";

        // The library folder of the video; null while its storage cannot be reached.
        public async Task<SubtitleFolder?> FolderAsync(LogicContext context, SubtitleSourceData source, CancellationToken ct)
        {
            try
            {
                var mount = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadStorageMountAsync(source.StorageMountId, token), ct);
                var files = storage.Open(mount);
                return await files.GetAsync(source.RootRelativePath, ct) is { IsDirectory: true } ? new SubtitleFolder(files, source.RootRelativePath) : null;
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        // An existing file of that name stays and false is returned; the new one belongs to the video's asset.
        public static async Task<bool> SaveAsync(LogicContext context, SubtitleSourceData source, SubtitleFolder folder, string relativePath, byte[] bytes, CancellationToken ct,
            long? accountId = null)
        {
            using var content = new MemoryStream(bytes);
            if (await LibraryFileWriter.WriteAsync(folder.Files, folder.Directory, relativePath, content, ct) is not var (length, modifiedAt))
            {
                return false;
            }

            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.SaveSubtitleFileAsync(new SubtitleFileData(source.LibraryRootId, relativePath, length, modifiedAt, source.MediaAssetId), token);
                if (await ctx.ReadStoredFileIdAsync(new LibraryFilePathData(source.LibraryRootId, relativePath), token) is { } file)
                {
                    await ctx.RecordActivityAsync(new ActivityEventData(ActivityEventType.SubtitleDownloaded, storedFileId: file.Id, accountId: accountId), token);
                }

                return true;
            }, ct);
            return true;
        }

        // Shared words of the release names, the group counting most.
        public static int Similarity(string? offer, SubtitleSourceData source)
        {
            if (offer is null || source.ReleaseName is null)
            {
                return 0;
            }

            var words = Words(source.ReleaseName);
            var score = Words(offer).Count(words.Contains);
            return source.ReleaseGroup is { } group && offer.Contains(group, StringComparison.OrdinalIgnoreCase) ? score + 10 : score;
        }

        private static HashSet<string> Words(string name) => [.. name.Split([' ', '.', '-', '_', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries).Select(word => word.ToLowerInvariant())];

        // The instance's wanted subtitle languages.
        public static async Task<string[]> ReadLanguagesAsync(LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."SubtitleLanguages"::text[] AS "SubtitleLanguages"
                FROM
                    "InstanceSettings" AS settings
                WHERE
                    settings."Id" = 1;
                """;
            return (await ctx.ReadSql.ReadRequiredAsync<SubtitleLanguagesData>(sql, ct)).SubtitleLanguages;
        }

        // Identified videos with a provider id, on storage that is not known to be offline: the chosen one, or the next not looked at for a day.
        public static Task<SubtitleSourceData?> ReadSourceAsync(LogicContext ctx, SubtitleSourceParameters data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."Id",
                    file."LibraryRootId",
                    file."SizeBytes",
                    file."ModifiedAt",
                    file."RelativePath",
                    file."ReleaseName",
                    file."ReleaseGroup",
                    file."MediaAssetId",
                    root."RelativePath" AS "RootRelativePath",
                    mount."Id" AS "StorageMountId",
                    work."MediaTypeId",
                    unit."Number" AS "UnitNumber",
                    parent."Number" AS "ParentUnitNumber",
                    (
                        SELECT
                            identity."Value"
                        FROM
                            "WorkExternalIdentities" AS identity
                        WHERE
                            identity."WorkId" = work."Id"
                            AND identity."Namespace" = 'imdb'
                    ) AS "ImdbId",
                    (
                        SELECT
                            identity."Value"
                        FROM
                            "WorkExternalIdentities" AS identity
                        WHERE
                            identity."WorkId" = work."Id"
                            AND identity."Namespace" IN ('tmdb_movie', 'tmdb_tv')
                        LIMIT 1
                    ) AS "TmdbId",
                    ARRAY(
                        SELECT
                            track."Language"::text
                        FROM
                            "MediaTracks" AS track
                        WHERE
                            track."StoredFileId" = file."Id"
                            AND track."MediaTrackTypeId" = 3
                            AND track."Language" IS NOT NULL
                            AND NOT track."IsForced"
                            AND lower(track."Codec") IN ('subrip', 'srt', 'ass', 'ssa', 'webvtt', 'mov_text', 'text')
                    ) AS "TrackLanguages",
                    ARRAY(
                        SELECT
                            other."RelativePath"::text
                        FROM
                            "StoredFiles" AS other
                        WHERE
                            other."LibraryRootId" = file."LibraryRootId"
                            AND other."Id" <> file."Id"
                            AND other."MissingSince" IS NULL
                            AND starts_with(other."RelativePath", regexp_replace(file."RelativePath", '\.[^./]+$', '') || '.')
                    ) AS "SiblingPaths"
                FROM
                    "StoredFiles" AS file
                    JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId" AND asset."MediaAssetTypeId" = 1
                    JOIN "Works" AS work ON work."Id" = asset."WorkId" AND work."MediaTypeId" IN (1, 2)
                    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                    LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                    LEFT JOIN "StorageMountStates" AS state ON state."StorageMountId" = mount."Id"
                WHERE
                    (file."Id" = @StoredFileId OR @StoredFileId IS NULL AND (file."SubtitlesCheckedAt" IS NULL OR file."SubtitlesCheckedAt" < now() - interval '1 day'))
                    AND file."ProbedAt" IS NOT NULL
                    AND file."MissingSince" IS NULL
                    AND NOT file."IsSubtitle"
                    AND coalesce(state."StorageAvailabilityTypeId", 1) = 1
                    AND (work."MediaTypeId" = 1 OR unit."WorkUnitTypeId" = 2 AND parent."Number" IS NOT NULL)
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "WorkExternalIdentities" AS identity
                        WHERE
                            identity."WorkId" = work."Id"
                            AND identity."Namespace" IN ('imdb', 'tmdb_movie', 'tmdb_tv')
                    )
                ORDER BY
                    file."SubtitlesCheckedAt" NULLS FIRST,
                    file."Id"
                LIMIT 1;
                """;
            sql.Parameters.Add(data);
            return ctx.ReadSql.ReadOptionalAsync<SubtitleSourceData>(sql, ct);
        }
    }

    // A video's library folder on its connection.
    internal sealed record SubtitleFolder(StorageFiles Files, string Directory);
}
