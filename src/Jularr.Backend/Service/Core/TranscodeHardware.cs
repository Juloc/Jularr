using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Media;
using Jularr.Integrations.Logic.Media;

namespace Jularr.Backend.Service.Core
{
    // What this machine offers for converting video; asked of FFmpeg once and kept until a benchmark looks again.
    internal sealed class TranscodeHardware(TranscodeBenchmarkClient client) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IReadOnlyList<TranscodeHardwareData>? _detected;

        public async Task<IReadOnlyList<TranscodeHardwareData>> GetAsync(CancellationToken ct, bool refresh = false)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (refresh || _detected is null)
                {
                    try
                    {
                        _detected = client.IsAvailable ? await client.DetectAsync(ct) : [];
                    }
                    catch (ProviderUnavailableException)
                    {
                        // FFmpeg that does not start offers nothing.
                        _detected = [];
                    }
                }

                return _detected;
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose() => _gate.Dispose();
    }
}
