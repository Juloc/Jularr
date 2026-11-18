using System.Text.Json;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;

namespace Jularr.Backend.Service.System.Acquisition.V1
{
    // A running pass of Jularr's own Usenet or torrent downloader: it shows its progress as it goes and learns at each report whether to stop. A pause the
    // Owner set, or a download that was taken out, ends the pass like its deadline; what it finished is kept and a resume continues from there.
    internal sealed class PassProgress(LogicContext context, long operationId, CancellationToken ct) : IDisposable
    {
        private readonly CancellationTokenSource _finish = CancellationTokenSource.CreateLinkedTokenSource(ct);
        private readonly global::System.Diagnostics.Stopwatch _elapsed = global::System.Diagnostics.Stopwatch.StartNew();
        private long? _firstDone;

        public CancellationToken Finish => _finish.Token;

        public async Task ReportAsync(DownloadStateType stage, long done, long size)
        {
            _firstDone ??= done;
            var remaining = Math.Max(0, size - done);
            var speed = (done - _firstDone.Value) / Math.Max(0.001, _elapsed.Elapsed.TotalSeconds);
            var progress = new[] { new { OperationId = operationId, SizeBytes = (long?)size, RemainingBytes = (long?)remaining,
                ProgressPercent = stage == DownloadStateType.Downloading && size > 0 ? (int?)Math.Clamp(done * 100 / size, 0, 99) : null,
                EtaSeconds = stage == DownloadStateType.Downloading && speed > 0 && remaining > 0 ? (int?)Math.Min(int.MaxValue, Math.Ceiling(remaining / speed)) : null,
                DownloadStateTypeId = (byte)stage, KeepsPause = true } };
            var goesOn = await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.UpdateDownloadProgressAsync(new DownloadProgressData(JsonSerializer.Serialize(progress)), token);
                return (await ctx.ReadActiveDownloadsAsync(token, operationId)).FirstOrDefault() is { IsPaused: false };
            }, ct);
            if (!goesOn) await _finish.CancelAsync();
        }

        public void Dispose() => _finish.Dispose();
    }
}
