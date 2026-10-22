using Microsoft.Extensions.Hosting;
using System.Threading.Channels;
using Jularr.Integrations.Logic.Translation;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Core
{
    public sealed class LocalTranslationOptions
    {
        // The separately deployed private Ollama runtime; never a user-provided URL.
        public string? Endpoint { get; set; }

        // An operator-confirmed budget for Ollama's own volume, never Jularr's cgroup.
        // 0 means no model downloads are allowed. Can be set only after checking the runtime.
        public long OperatorVerifiedFreeBytes { get; set; }
    }

    internal sealed class LocalTranslationGate
    {
        public SemaphoreSlim HeavyWork { get; } = new(1, 1);
    }

    // Model download work is detached from the HTTP request and owned by the host.
    // Ollama retains partial blobs on interruption; a retry explicitly resumes via /api/pull.
    internal sealed class LocalTranslationDownloads(OllamaLocalModels models, IOptions<LocalTranslationOptions> settings,
        LocalTranslationGate gate) : BackgroundService
    {
        private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });
        private readonly object _guard = new();
        private string? _active;
        private string _lastModel = "";
        private string _state = "idle";
        private int _progress;

        public (string Model, string State, int Progress) Current()
        {
            lock (_guard) return (_active ?? _lastModel, _state, _progress);
        }

        public bool Queue(string model)
        {
            if (!OllamaLocalModels.Supported(model)) return false;
            // Ollama's own persistent volume is not visible inside the Jularr container.
            // Fail closed unless the operator has explicitly verified sufficient capacity.
            var size = model switch
            {
                "translategemma:4b" => 3_300_000_000L,
                "translategemma:12b" => 8_100_000_000L,
                "translategemma:27b" => 17_000_000_000L,
                _ => long.MaxValue
            };
            if (settings.Value.OperatorVerifiedFreeBytes < size + 2_000_000_000L) return false;
            lock (_guard)
            {
                if (_active is not null) return false;
                _active = model;
                _lastModel = model;
                _state = "queued";
                _progress = 0;
                if (_queue.Writer.TryWrite(model)) return true;
                _active = null;
                _state = "idle";
                return false;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (var model in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    var server = OllamaLocalModels.Endpoint(settings.Value.Endpoint);
                    if (server is null) throw new InvalidOperationException("Local server endpoint is missing.");
                    await gate.HeavyWork.WaitAsync(stoppingToken);
                    try
                    {
                        Set("downloading", 0);
                        await models.PullAsync(server, model, stoppingToken, fraction => Set("downloading", fraction));
                        Set("installed", 100);
                    }
                    finally { gate.HeavyWork.Release(); }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    Set("interrupted", 0);
                    break;
                }
                catch (Exception)
                {
                    Set("failed", 0);
                }
                finally { lock (_guard) _active = null; }
            }
        }

        private void Set(string state, int progress)
        {
            lock (_guard)
            {
                _state = state;
                _progress = Math.Clamp(progress, 0, 100);
            }
        }
    }
}
