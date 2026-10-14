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

            // Analysed videos on storage that is not known to be offline; failed ones again after a day.
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
                        mount."SharePath",
                        video."Width",
                        video."Height"
                    FROM
                        "StoredFiles" AS file
                        JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId" AND asset."MediaAssetTypeId" = 1
                        JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                        JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                        LEFT JOIN "StorageMountStates" AS state ON state."StorageMountId" = mount."Id"
                        JOIN LATERAL (
                            SELECT
                                track."Width",
                                track."Height"
                            FROM
                                "MediaTracks" AS track
                            WHERE
                                track."StoredFileId" = file."Id"
                                AND track."MediaTrackTypeId" = 1
                                AND track."Width" > 0
                                AND track."Height" > 0
                            ORDER BY
                                track."StreamIndex"
                            LIMIT 1
                        ) AS video ON TRUE
                    WHERE
                        file."TrickplayFrames" IS NULL
                        AND (file."TrickplayCheckedAt" IS NULL OR file."TrickplayCheckedAt" < now() - interval '1 day')
                        AND file."ProbedAt" IS NOT NULL
                        AND file."DurationMs" > 0
                        AND file."MissingSince" IS NULL
                        AND NOT file."IsSubtitle"
                        AND coalesce(state."StorageAvailabilityTypeId", 1) = 1
                    ORDER BY
                        file."TrickplayCheckedAt" NULLS FIRST,
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
                // Unreachable storage counts as an attempt, so other files go first.
                await SaveAsync(source, null);
                return new UpdateResult(true);
            }

            // Rendered aside, then swapped in whole; the old sheets leave only after the new ones are in place.
            var folder = Trickplay.Folder(root, source.Id);
            var pending = folder + ".partial";
            var replaced = folder + ".old";
            foreach (var leftover in (string[])[pending, replaced])
            {
                if (Directory.Exists(leftover)) Directory.Delete(leftover, true);
            }

            Directory.CreateDirectory(pending);
            int? frames = null;
            if (await _transcoder.CreateTrickplayAsync(input, pending, Trickplay.IntervalSeconds, Trickplay.TileWidth, Trickplay.TileHeight(source.Width, source.Height),
                    Trickplay.Columns, Trickplay.Rows, CancellationToken)
                && Directory.GetFiles(pending, "*.jpg").Length is var sheets and > 0)
            {
                if (Directory.Exists(folder)) Directory.Move(folder, replaced);
                Directory.Move(pending, folder);
                if (Directory.Exists(replaced)) Directory.Delete(replaced, true);
                var tiles = (int)Math.Ceiling(source.DurationMs / (Trickplay.IntervalSeconds * 1000d));
                frames = Math.Min(tiles, sheets * Trickplay.Columns * Trickplay.Rows);
            }
            else
            {
                Directory.Delete(pending, true);
            }

            await SaveAsync(source, frames);
            return new UpdateResult(true);
        }

        private Task<bool> SaveAsync(TrickplaySourceData source, int? frames) =>
            Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SaveTrickplayAsync(new TrickplaySaveData(source.Id, source.SizeBytes, source.ModifiedAt, frames), ct);
                return true;
            }, CancellationToken);
    }
}
