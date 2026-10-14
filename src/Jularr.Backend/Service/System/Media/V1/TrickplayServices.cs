using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Media;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.System.Media.V1
{
    // Renders the seek thumbnails of one video file per call; returns whether there was one.
    public sealed class GenerateTrickplayV1 : SystemService<TrickplayParametersV1, NoData>
    {
        private readonly MediaOptions _media;
        private readonly StorageOptions _storage;
        private readonly MediaTranscoder _transcoder;

        internal GenerateTrickplayV1(IOptions<MediaOptions> media, IOptions<StorageOptions> storage, MediaTranscoder transcoder)
        {
            _media = media.Value;
            _storage = storage.Value;
            _transcoder = transcoder;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(TrickplayParametersV1 parameters) => ModuleRequirement.All("Library", "Playback", "Trickplay");
        protected override ServicePermission GetPermission(TrickplayParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (_media.TrickplayPath is not { } root || !_transcoder.IsAvailable)
            {
                return new UpdateResult(false);
            }

            // Analysed videos on storage that is not known to be offline.
            var source = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        file."Id",
                        file."SizeBytes",
                        file."ModifiedAt",
                        file."DurationMs",
                        file."RelativePath",
                        root."RelativePath" AS "RootRelativePath",
                        mount."Id" AS "StorageMountId",
                        mount."StorageProtocolTypeId",
                        mount."SharePath"
                    FROM
                        "StoredFiles" AS file
                        JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId" AND asset."MediaAssetTypeId" = 1
                        JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                        JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                        LEFT JOIN "StorageMountStates" AS state ON state."StorageMountId" = mount."Id"
                    WHERE
                        file."TrickplayCheckedAt" IS NULL
                        AND file."ProbedAt" IS NOT NULL
                        AND file."DurationMs" > 0
                        AND file."MissingSince" IS NULL
                        AND file."RelativePath" !~* '\.(srt|ass|ssa|vtt|sub|idx|sup)$'
                        AND coalesce(state."StorageAvailabilityTypeId", 1) = 1
                    ORDER BY
                        file."Id"
                    LIMIT 1;
                    """;
                return ctx.ReadSql.ReadOptionalAsync<TrickplaySourceData>(sql, ct);
            }, CancellationToken);
            if (source is null)
            {
                return new UpdateResult(false);
            }

            string input;
            try
            {
                var directory = StorageFileSystem.ResolveDirectory(_storage.GetConnectionPath(source.StorageProtocolTypeId, source.StorageMountId, source.SharePath), source.RootRelativePath);
                input = StorageFileSystem.ResolveFile(directory, source.RelativePath);
            }
            catch (NasMountException)
            {
                // Storage that went away is tried again with the next pass.
                return new UpdateResult(false);
            }

            // Rendered aside, then swapped in whole.
            var folder = Trickplay.Folder(root, source.Id);
            var pending = folder + ".partial";
            if (Directory.Exists(pending)) Directory.Delete(pending, true);
            Directory.CreateDirectory(pending);
            int? frames = null;
            if (await _transcoder.CreateTrickplayAsync(input, pending, Trickplay.IntervalSeconds, Trickplay.TileWidth, CancellationToken)
                && Directory.GetFiles(pending, "*.jpg").Length is var sheets and > 0)
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                Directory.Move(pending, folder);
                var tiles = (int)Math.Ceiling(source.DurationMs / (Trickplay.IntervalSeconds * 1000d));
                frames = Math.Min(tiles, sheets * Trickplay.Columns * Trickplay.Rows);
            }
            else
            {
                Directory.Delete(pending, true);
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SaveTrickplayAsync(new TrickplaySaveData(source.Id, source.SizeBytes, source.ModifiedAt, frames), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
