using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.User.Playback.V1
{
    // The played file of an asset as a profile may read it: the file, its mount and its folder, for taking fonts out of it.
    internal static class PlaybackFontSources
    {
        public static async Task<PlaybackSubtitleSourceData> FindAsync(Func<SqlStatement, Task<PlaybackSubtitleSourceData?>> read, long mediaAssetId, long profileId)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."Id" AS "StoredFileId",
                    file."RelativePath",
                    file."ModifiedAt",
                    root."RelativePath" AS "RootRelativePath",
                    mount."Id" AS "StorageMountId",
                    NULL::varchar AS "Codec"
                FROM
                    "MediaAssets" AS asset
                    JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                WHERE
                    asset."Id" = @MediaAssetId
                    AND file."MissingSince" IS NULL
                    AND NOT file."IsSubtitle"
                ORDER BY
                    file."Id"
                LIMIT 1;
                """;
            sql.Parameters.Add(new PlaybackSubtitleLookupData(mediaAssetId, profileId, null, null));
            return await read(sql) ?? throw new KeyNotFoundException("The file was not found.");
        }

        public static string CacheKey(PlaybackSubtitleSourceData source) =>
            source.StoredFileId.ToString(global::System.Globalization.CultureInfo.InvariantCulture) + "-" + source.ModifiedAt.Ticks.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
    }

    // The fonts the played file carries, for the player's libass: a list first, then each font as bytes.
    [ServiceEndpoint("GET", "/api/v1/playback/assets/{mediaAssetId:long}/fonts")]
    public sealed class ReadPlaybackFontsV1 : UserService<MediaAssetParametersV1>
    {
        private readonly StorageAccess _storage;
        private readonly PlaybackFonts _fonts;

        internal ReadPlaybackFontsV1(StorageAccess storage, PlaybackFonts fonts)
        {
            _storage = storage;
            _fonts = fonts;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<PlaybackFontV1>>()];
        protected override ModuleRequirement GetInstanceModules(MediaAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(MediaAssetParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            var source = await PlaybackFontSources.FindAsync(sql => Context.ReadSql.ReadOptionalAsync<PlaybackSubtitleSourceData>(sql, CancellationToken), Parameters.MediaAssetId, Auth.ProfileId!.Value);
            var files = _storage.Open(await Context.ReadStorageMountAsync(source.StorageMountId, CancellationToken));
            var names = await _fonts.ListAsync(files, StoragePath.Combine(source.RootRelativePath, source.RelativePath), PlaybackFontSources.CacheKey(source), CancellationToken);
            return new BaseDataList<PlaybackFontV1>([.. names.OrderBy(item => item.Key).Select(item => new PlaybackFontV1(item.Key, item.Value))]);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/playback/assets/{mediaAssetId:long}/fonts/{key:int}")]
    public sealed class ReadPlaybackFontV1 : UserService<PlaybackFontParametersV1>
    {
        private readonly PlaybackFonts _fonts;

        internal ReadPlaybackFontV1(PlaybackFonts fonts)
        {
            _fonts = fonts;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlaybackStreamV1>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackFontParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackFontParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Only a font that was taken out of this profile's file by the list is served, from the font folder and by its number.
            var source = await PlaybackFontSources.FindAsync(sql => Context.ReadSql.ReadOptionalAsync<PlaybackSubtitleSourceData>(sql, CancellationToken), Parameters.MediaAssetId, Auth.ProfileId!.Value);
            var path = Path.Combine(_fonts.FolderOf(PlaybackFontSources.CacheKey(source)), Parameters.Key.ToString(global::System.Globalization.CultureInfo.InvariantCulture) + ".font");
            return File.Exists(path) ? new PlaybackStreamV1(File.OpenRead(path), "font/ttf") : throw new KeyNotFoundException("The font was not found.");
        }
    }
}
