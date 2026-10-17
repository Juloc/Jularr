using System.Text.Json;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Data.Storage;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.System.Operation.V1
{
    // Runs after the canonical media scan and assignment; image candidates never become StoredFiles.
    // No remote provider calls, image decoding or NAS writes occur in this scan.
    internal static class ArtworkSidecarScan
    {
        private const int BatchSize = 100;
        private const long MaximumImageBytes = 20 * 1024 * 1024;

        public static async Task<bool> RunAsync(
            LogicContext context, StorageFiles files, string rootPath, long libraryRootId, DateTime seenAt, CancellationToken ct)
        {
            var candidates = new List<ArtworkScanCandidate>(BatchSize);
            var scanState = new StorageScanState();

            await foreach (var file in files.EnumerateFilesAsync(rootPath, ArtworkFileNames.ScanExtensions, scanState, ct))
            {
                if (file.SizeBytes <= 0 || file.SizeBytes > MaximumImageBytes || file.RelativePath.Length > 2048)
                {
                    continue;
                }

                var sidecar = ArtworkFileNames.Recognize(file.RelativePath);
                if (sidecar is null)
                {
                    continue;
                }

                var slash = file.RelativePath.LastIndexOf('/');
                candidates.Add(new ArtworkScanCandidate(file.RelativePath, slash < 0 ? "" : file.RelativePath[..slash],
                    (short)sidecar.Value.Slot, sidecar.Value.SeasonNumber));
                if (candidates.Count >= BatchSize)
                {
                    await SaveAsync(context, libraryRootId, seenAt, candidates, ct);
                }
            }

            if (!scanState.IsComplete)
            {
                return false;
            }

            await SaveAsync(context, libraryRootId, seenAt, candidates, ct);
            var completed = new ArtworkScanBatchData(libraryRootId, seenAt, "[]");
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.CompleteSidecarScanAsync(completed, token);
                return true;
            }, ct);
            return true;
        }

        private static async Task SaveAsync(LogicContext context, long libraryRootId, DateTime seenAt,
            List<ArtworkScanCandidate> candidates, CancellationToken ct)
        {
            if (candidates.Count == 0)
            {
                return;
            }

            var data = new ArtworkScanBatchData(libraryRootId, seenAt, JsonSerializer.Serialize(candidates));
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.UpsertSidecarsAsync(data, token);
                return true;
            }, ct);
            candidates.Clear();
        }
    }
}
