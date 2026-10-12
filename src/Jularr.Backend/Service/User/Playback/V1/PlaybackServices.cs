using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Nas;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.User.Playback.V1
{
    [ServiceEndpoint("GET", "/api/v1/playback/assets/{mediaAssetId:long}")]
    public sealed class ReadPlaybackItemV1 : UserService<MediaAssetParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlaybackItemV1>()];
        protected override ModuleRequirement GetInstanceModules(MediaAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(MediaAssetParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Canonical identity and this profile's resume point.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    asset."Id" AS "MediaAssetId",
                    work."Id" AS "WorkId",
                    work."CanonicalTitle",
                    asset."MediaAssetTypeId",
                    unit."WorkUnitTypeId",
                    unit."Number" AS "UnitNumber",
                    parent."Number" AS "ParentUnitNumber",
                    progress."PositionMs" AS "ResumePositionMs"
                FROM
                    "MediaAssets" AS asset
                    JOIN "Works" AS work ON work."Id" = asset."WorkId"
                    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                    LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                    LEFT JOIN "MediaProgress" AS progress ON progress."ProfileId" = @ProfileId
                        AND progress."WorkId" = asset."WorkId"
                        AND progress."WorkUnitId" IS NOT DISTINCT FROM asset."WorkUnitId"
                WHERE
                    asset."Id" = @MediaAssetId;
                """;
            sql.Parameters.Add(new PlaybackSourceParameters(Parameters.MediaAssetId, Auth.ProfileId!.Value));
            return await Context.ReadSql.ReadRequiredAsync<PlaybackItemV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/playback/assets/{mediaAssetId:long}/sessions")]
    public sealed class StartPlaybackV1 : UserService<MediaAssetParametersV1, StartPlaybackDataV1>
    {
        private readonly StorageOptions _options;
        private readonly WakeOnLanClient _wake;

        internal StartPlaybackV1(IOptions<StorageOptions> options, WakeOnLanClient wake)
        {
            _options = options.Value;
            _wake = wake;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlaybackPlanV1>()];
        protected override ModuleRequirement GetInstanceModules(MediaAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(MediaAssetParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Stored source, streams and resume point.
            var lookup = new PlaybackSourceParameters(Parameters.MediaAssetId, Auth.ProfileId!.Value);
            var (source, tracks) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        file."Id" AS "StoredFileId",
                        file."RelativePath",
                        root."RelativePath" AS "RootRelativePath",
                        mount."Id" AS "StorageMountId",
                        mount."StorageProtocolTypeId",
                        mount."SharePath",
                        mount."WakeMacAddress",
                        mount."WakeBroadcastAddress",
                        mount."WakeOnDemand",
                        file."DurationMs",
                        progress."PositionMs" AS "ResumePositionMs"
                    FROM
                        "MediaAssets" AS asset
                        JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                        JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                        JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                        LEFT JOIN "MediaProgress" AS progress ON progress."ProfileId" = @ProfileId
                            AND progress."WorkId" = asset."WorkId"
                            AND progress."WorkUnitId" IS NOT DISTINCT FROM asset."WorkUnitId"
                    WHERE
                        asset."Id" = @MediaAssetId
                        AND file."MissingSince" IS NULL
                        AND file."RelativePath" !~* '\.(srt|ass|ssa|vtt|sub|idx|sup)$'
                    ORDER BY
                        file."Id"
                    LIMIT 1;
                    """;
                sql.Parameters.Add(lookup);
                var source = await ctx.ReadSql.ReadOptionalAsync<PlaybackSourceData>(sql, ct) ?? throw new KeyNotFoundException("No playable file is available.");

                sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        track."MediaTrackTypeId",
                        track."Codec",
                        track."IsDefault"
                    FROM
                        "MediaTracks" AS track
                    WHERE
                        track."StoredFileId" = @Id
                    ORDER BY
                        track."StreamIndex";
                    """;
                sql.Parameters.Add(new StoredFileIdData(source.StoredFileId));
                return (source, await ctx.ReadSql.ReadListAsync<PlaybackTrackData>(sql, ct));
            }, CancellationToken);

            // Delivery follows the client's actual capabilities.
            var stream = LibraryFileTypes.GetStream(source.RelativePath);
            var mode = PlaybackLogic.Decide(stream.Container, tracks, Data.Containers.ToHashSet(StringComparer.OrdinalIgnoreCase),
                Data.VideoCodecs.ToHashSet(StringComparer.OrdinalIgnoreCase), Data.AudioCodecs.ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (mode != PlaybackModeType.DirectPlay)
            {
                throw new ServiceUnavailableException("playback_conversion_unavailable");
            }

            // Sleeping storage wakes only when the Owner allowed it.
            try
            {
                var directory = StorageFileSystem.ResolveDirectory(_options.GetConnectionPath(source.StorageProtocolTypeId, source.StorageMountId, source.SharePath), source.RootRelativePath);
                StorageFileSystem.ResolveFile(directory, source.RelativePath);
            }
            catch (NasMountException) when (source is { WakeOnDemand: true, WakeMacAddress: { } macAddress })
            {
                await _wake.SendAsync(macAddress, source.WakeBroadcastAddress, CancellationToken);
                throw new ServiceUnavailableException("storage_waking");
            }

            var resume = source.ResumePositionMs ?? 0;
            var sessionId = await Context.ExecuteBatchAsync((ctx, ct) =>
                ctx.CreatePlaybackSessionAsync(new PlaybackSessionCreationData(lookup.ProfileId, Parameters.MediaAssetId, source.StoredFileId, mode, resume), ct), CancellationToken);
            return new PlaybackPlanV1(sessionId, mode, "/api/v1/playback/sessions/" + sessionId.ToString("D") + "/stream", resume, source.DurationMs);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/playback/sessions/{playbackSessionId:guid}/stream")]
    public sealed class ReadPlaybackStreamV1 : UserService<PlaybackSessionParametersV1>
    {
        private readonly StorageOptions _options;

        internal ReadPlaybackStreamV1(IOptions<StorageOptions> options)
        {
            _options = options.Value;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlaybackStreamV1>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackSessionParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackSessionParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Only the owning profile's active session streams.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."RelativePath",
                    root."RelativePath" AS "RootRelativePath",
                    mount."Id" AS "StorageMountId",
                    mount."StorageProtocolTypeId",
                    mount."SharePath"
                FROM
                    "PlaybackSessions" AS session
                    JOIN "StoredFiles" AS file ON file."Id" = session."StoredFileId"
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                WHERE
                    session."PublicId" = @PlaybackSessionId
                    AND session."ProfileId" = @ProfileId
                    AND session."EndedAt" IS NULL;
                """;
            sql.Parameters.Add(new PlaybackSessionLookupData(Parameters.PlaybackSessionId, Auth.ProfileId!.Value));
            var stream = await Context.ReadSql.ReadRequiredAsync<PlaybackStreamData>(sql, CancellationToken);

            // The HTTP adapter opens the file after this read.
            var connection = Path.TrimEndingDirectorySeparator(_options.GetConnectionPath(stream.StorageProtocolTypeId, stream.StorageMountId, stream.SharePath));
            var path = Path.GetFullPath(Path.Combine(connection, stream.RootRelativePath, stream.RelativePath));
            if (!path.StartsWith(connection + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new KeyNotFoundException("The playback file is unavailable.");
            }

            return new PlaybackStreamV1(path, LibraryFileTypes.GetStream(stream.RelativePath).ContentType);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/playback/sessions/{playbackSessionId:guid}")]
    public sealed class UpdatePlaybackV1 : UserService<PlaybackSessionParametersV1, UpdatePlaybackDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackSessionParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackSessionParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var data = new PlaybackUpdateData(Parameters.PlaybackSessionId, Auth.ProfileId!.Value, Data.PositionMs, Data.DurationMs, Data.IsEnded, Data.IsCompleted);
            await Context.UpdatePlaybackAsync(data, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
