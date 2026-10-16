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

            var files = _storage.Open(await Context.ReadStorageMountAsync(stream.StorageMountId, CancellationToken));
            return new GameFileV1(await files.OpenReadAsync(StoragePath.Combine(stream.RootRelativePath, stream.RelativePath), CancellationToken));
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
