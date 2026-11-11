using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Game.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.User.Game.V1
{
    [ServiceEndpoint("GET", "/api/v1/games/assets/{mediaAssetId:long}")]
    public sealed class ReadGameV1 : UserService<GameParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<GameV1>()];
        protected override ModuleRequirement GetInstanceModules(GameParametersV1 parameters) => ModuleRequirement.All("Library", "Emulation");
        protected override ServicePermission GetPermission(GameParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The game file, its platform and the profile's play time.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    asset."Id" AS "MediaAssetId",
                    work."Id" AS "WorkId",
                    work."CanonicalTitle" AS "Title",
                    file."RelativePath",
                    platform."IgdbId",
                    CASE WHEN progress."ProgressPositionTypeId" = 3 THEN coalesce(progress."PositionMs", 0) ELSE 0 END AS "PlayedMs"
                FROM
                    "MediaAssets" AS asset
                    JOIN "Works" AS work ON work."Id" = asset."WorkId"
                    JOIN LATERAL (
                        SELECT
                            candidate."RelativePath"
                        FROM
                            "StoredFiles" AS candidate
                        WHERE
                            candidate."MediaAssetId" = asset."Id"
                            AND candidate."MissingSince" IS NULL
                        ORDER BY
                            candidate."Id"
                        LIMIT 1
                    ) AS file ON TRUE
                    LEFT JOIN "GameReleases" AS release ON release."WorkVersionId" = asset."WorkVersionId"
                    LEFT JOIN "GamePlatforms" AS platform ON platform."Id" = release."GamePlatformId"
                    LEFT JOIN "MediaProgress" AS progress ON progress."ProfileId" = @ProfileId
                        AND progress."WorkId" = asset."WorkId"
                        AND progress."WorkUnitId" IS NOT DISTINCT FROM asset."WorkUnitId"
                WHERE
                    asset."Id" = @MediaAssetId
                    AND asset."MediaAssetTypeId" = 5
                    AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId);
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            var game = await Context.ReadSql.ReadRequiredAsync<GameSourceData>(sql, CancellationToken);
            return new GameV1(game.MediaAssetId, game.WorkId, game.Title, Path.GetFileName(game.RelativePath), GameRomMatching.Emulated(game.IgdbId)?.Emulator, game.PlayedMs);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/games/assets/{mediaAssetId:long}/files/{fileName}")]
    public sealed class ReadGameFileV1 : UserService<GameFileParametersV1>
    {
        private readonly StorageAccess _storage;

        internal ReadGameFileV1(StorageAccess storage)
        {
            _storage = storage;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<GameFileV1>()];
        protected override ModuleRequirement GetInstanceModules(GameFileParametersV1 parameters) => ModuleRequirement.All("Library", "Emulation");
        protected override ServicePermission GetPermission(GameFileParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The emulated game's own file, under the name it has in the library.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."RelativePath",
                    root."RelativePath" AS "RootRelativePath",
                    mount."Id" AS "StorageMountId"
                FROM
                    "MediaAssets" AS asset
                    JOIN "GameReleases" AS release ON release."WorkVersionId" = asset."WorkVersionId"
                    JOIN "GamePlatforms" AS platform ON platform."Id" = release."GamePlatformId"
                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                WHERE
                    asset."Id" = @MediaAssetId
                    AND asset."MediaAssetTypeId" = 5
                    AND platform."IgdbId" = ANY(@EmulatedPlatforms)
                    AND file."MissingSince" IS NULL
                    AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId)
                ORDER BY
                    file."Id"
                LIMIT 1;
                """;
            sql.Parameters.Add(new GameLookupData(Auth.ProfileId!.Value, Parameters.MediaAssetId, GameRomMatching.EmulatedPlatforms));
            var stream = await Context.ReadSql.ReadRequiredAsync<PlaybackStreamData>(sql, CancellationToken);
            if (!string.Equals(Path.GetFileName(stream.RelativePath), Parameters.FileName, StringComparison.Ordinal))
            {
                throw new KeyNotFoundException("The game file was not found.");
            }

            var mount = await Context.ReadStorageMountAsync(stream.StorageMountId, CancellationToken);
            await Context.EndReadAsync(CancellationToken);
            var files = _storage.Open(mount);
            return new GameFileV1(await files.OpenReadAsync(StoragePath.Combine(stream.RootRelativePath, stream.RelativePath), CancellationToken));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/games/files")]
    public sealed class ReadGameFilesV1 : UserService<ReadGameFilesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<GameLibraryFileV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadGameFilesParametersV1 parameters) => ModuleRequirement.All("Library", "Emulation");
        protected override ServicePermission GetPermission(ReadGameFilesParametersV1 parameters, Type resultType) => new("profile.playback");
        protected override PageRequest GetPageRequest(ReadGameFilesParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Every file of the games libraries: one with a game the profile sees, one without where the profile may see games at all.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."Id" AS "StoredFileId",
                    file."RelativePath",
                    file."SizeBytes",
                    platform."IgdbId",
                    asset."Id" AS "MediaAssetId",
                    work."Id" AS "WorkId",
                    work."CanonicalTitle" AS "Title"
                FROM
                    "StoredFiles" AS file
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    LEFT JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                    LEFT JOIN "Works" AS work ON work."Id" = asset."WorkId"
                    LEFT JOIN "GameReleases" AS release ON release."WorkVersionId" = asset."WorkVersionId"
                    LEFT JOIN LATERAL (
                        SELECT
                            catalog."GamePlatformId"
                        FROM
                            "GameRoms" AS rom
                            JOIN "GameRomCatalogs" AS catalog ON catalog."Id" = rom."GameRomCatalogId"
                        WHERE
                            rom."Sha1" = file."Sha1"
                        ORDER BY
                            rom."GameRomCatalogId"
                        LIMIT 1
                    ) AS dump ON TRUE
                    LEFT JOIN "GamePlatforms" AS platform ON platform."Id" = coalesce(release."GamePlatformId", dump."GamePlatformId")
                WHERE
                    root."MediaTypeId" = 7
                    AND file."MissingSince" IS NULL
                    AND NOT file."IsSubtitle"
                    AND CASE
                        WHEN asset."Id" IS NULL THEN EXISTS (
                            SELECT 1 FROM "ProfileMediaCategories" AS category WHERE category."ProfileId" = @ProfileId AND category."MediaTypeId" = 7 AND category."MediaSubtypeId" IS NULL)
                        ELSE EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId)
                    END
                ORDER BY
                    lower(file."RelativePath"),
                    file."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            sql.Page = Page;
            var rows = await Context.ReadSql.ReadPageAsync<GameLibraryFileData>(sql, CancellationToken);
            return new PageResult<GameLibraryFileV1>([.. rows.Items.Select(Describe)], Page);
        }

        internal static GameLibraryFileV1 Describe(GameLibraryFileData file)
        {
            var name = Path.GetFileName(file.RelativePath);
            var system = GameRomMatching.Played(file.IgdbId, name);
            return new GameLibraryFileV1(file.StoredFileId, name, file.SizeBytes, system?.Abbreviation, system?.Emulator, system is null && GameRomMatching.NeedsBios(name),
                file.MediaAssetId, file.WorkId, file.Title);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/games/files/{storedFileId:long}")]
    public sealed class ReadGameStoredFileV1 : UserService<GameStoredFileParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<GameLibraryFileV1>()];
        protected override ModuleRequirement GetInstanceModules(GameStoredFileParametersV1 parameters) => ModuleRequirement.All("Library", "Emulation");
        protected override ServicePermission GetPermission(GameStoredFileParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            // One file of a games library, as the list shows it.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."Id" AS "StoredFileId",
                    file."RelativePath",
                    file."SizeBytes",
                    platform."IgdbId",
                    asset."Id" AS "MediaAssetId",
                    work."Id" AS "WorkId",
                    work."CanonicalTitle" AS "Title"
                FROM
                    "StoredFiles" AS file
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    LEFT JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                    LEFT JOIN "Works" AS work ON work."Id" = asset."WorkId"
                    LEFT JOIN "GameReleases" AS release ON release."WorkVersionId" = asset."WorkVersionId"
                    LEFT JOIN LATERAL (
                        SELECT
                            catalog."GamePlatformId"
                        FROM
                            "GameRoms" AS rom
                            JOIN "GameRomCatalogs" AS catalog ON catalog."Id" = rom."GameRomCatalogId"
                        WHERE
                            rom."Sha1" = file."Sha1"
                        ORDER BY
                            rom."GameRomCatalogId"
                        LIMIT 1
                    ) AS dump ON TRUE
                    LEFT JOIN "GamePlatforms" AS platform ON platform."Id" = coalesce(release."GamePlatformId", dump."GamePlatformId")
                WHERE
                    file."Id" = @StoredFileId
                    AND root."MediaTypeId" = 7
                    AND file."MissingSince" IS NULL
                    AND NOT file."IsSubtitle"
                    AND CASE
                        WHEN asset."Id" IS NULL THEN EXISTS (
                            SELECT 1 FROM "ProfileMediaCategories" AS category WHERE category."ProfileId" = @ProfileId AND category."MediaTypeId" = 7 AND category."MediaSubtypeId" IS NULL)
                        ELSE EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId)
                    END;
                """;
            sql.Parameters.Add(new GameStoredFileLookupData(Auth.ProfileId!.Value, Parameters.StoredFileId));
            return ReadGameFilesV1.Describe(await Context.ReadSql.ReadRequiredAsync<GameLibraryFileData>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/games/files/{storedFileId:long}/content/{fileName}")]
    public sealed class ReadGameStoredFileContentV1 : UserService<GameStoredFileContentParametersV1>
    {
        private readonly StorageAccess _storage;

        internal ReadGameStoredFileContentV1(StorageAccess storage)
        {
            _storage = storage;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<GameFileV1>()];
        protected override ModuleRequirement GetInstanceModules(GameStoredFileContentParametersV1 parameters) => ModuleRequirement.All("Library", "Emulation");
        protected override ServicePermission GetPermission(GameStoredFileContentParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            // A file of a games library the browser plays, under its own name.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."RelativePath",
                    root."RelativePath" AS "RootRelativePath",
                    root."StorageMountId",
                    platform."IgdbId"
                FROM
                    "StoredFiles" AS file
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    LEFT JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                    LEFT JOIN "GameReleases" AS release ON release."WorkVersionId" = asset."WorkVersionId"
                    LEFT JOIN LATERAL (
                        SELECT
                            catalog."GamePlatformId"
                        FROM
                            "GameRoms" AS rom
                            JOIN "GameRomCatalogs" AS catalog ON catalog."Id" = rom."GameRomCatalogId"
                        WHERE
                            rom."Sha1" = file."Sha1"
                        ORDER BY
                            rom."GameRomCatalogId"
                        LIMIT 1
                    ) AS dump ON TRUE
                    LEFT JOIN "GamePlatforms" AS platform ON platform."Id" = coalesce(release."GamePlatformId", dump."GamePlatformId")
                WHERE
                    file."Id" = @StoredFileId
                    AND root."MediaTypeId" = 7
                    AND file."MissingSince" IS NULL
                    AND CASE
                        WHEN asset."Id" IS NULL THEN EXISTS (
                            SELECT 1 FROM "ProfileMediaCategories" AS category WHERE category."ProfileId" = @ProfileId AND category."MediaTypeId" = 7 AND category."MediaSubtypeId" IS NULL)
                        ELSE EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId)
                    END;
                """;
            sql.Parameters.Add(new GameStoredFileLookupData(Auth.ProfileId!.Value, Parameters.StoredFileId));
            var file = await Context.ReadSql.ReadRequiredAsync<GameStoredFileSourceData>(sql, CancellationToken);
            var name = Path.GetFileName(file.RelativePath);
            if (!string.Equals(name, Parameters.FileName, StringComparison.Ordinal) || GameRomMatching.Played(file.IgdbId, name) is null)
            {
                throw new KeyNotFoundException("The game file was not found.");
            }

            var mount = await Context.ReadStorageMountAsync(file.StorageMountId, CancellationToken);
            await Context.EndReadAsync(CancellationToken);
            var files = _storage.Open(mount);
            return new GameFileV1(await files.OpenReadAsync(StoragePath.Combine(file.RootRelativePath, file.RelativePath), CancellationToken));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/games/assets/{mediaAssetId:long}/saves/{gameSaveTypeId}")]
    public sealed class ReadGameSaveV1 : UserService<GameSaveParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<GameSaveV1>()];
        protected override ModuleRequirement GetInstanceModules(GameSaveParametersV1 parameters) => ModuleRequirement.All("Library", "Emulation");
        protected override ServicePermission GetPermission(GameSaveParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    save."Data",
                    save."UpdatedAt"
                FROM
                    "GameSaves" AS save
                WHERE
                    save."ProfileId" = @ProfileId
                    AND save."MediaAssetId" = @MediaAssetId
                    AND save."GameSaveTypeId" = @GameSaveTypeId;
                """;
            sql.Parameters.Add(new GameSaveLookupData(Auth.ProfileId!.Value, Parameters.MediaAssetId, Parameters.GameSaveTypeId));
            return await Context.ReadSql.ReadRequiredAsync<GameSaveV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/games/assets/{mediaAssetId:long}/saves/{gameSaveTypeId}")]
    public sealed class UpdateGameSaveV1 : UserService<GameSaveParametersV1, UpdateGameSaveDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(GameSaveParametersV1 parameters) => ModuleRequirement.All("Library", "Emulation");
        protected override ServicePermission GetPermission(GameSaveParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveGameAsync(new GameSaveWriteData(Auth.ProfileId!.Value, Parameters.MediaAssetId, Parameters.GameSaveTypeId, Data.Data, GameRomMatching.EmulatedPlatforms),
                CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/games/assets/{mediaAssetId:long}/play-time")]
    public sealed class RecordGamePlayV1 : UserService<GameParametersV1, RecordGamePlayDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(GameParametersV1 parameters) => ModuleRequirement.All("Library", "Emulation");
        protected override ServicePermission GetPermission(GameParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var played = new GamePlayData(Auth.ProfileId!.Value, Parameters.MediaAssetId, Data.Seconds, GameRomMatching.EmulatedPlatforms);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.RecordGamePlayAsync(played, ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
