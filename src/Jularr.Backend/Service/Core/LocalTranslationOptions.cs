using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Infrastructure.Sql;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Channels;
using Jularr.Integrations.Logic.Translation;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Core
{
    public sealed class LocalTranslationOptions
    {
        // The separately deployed private Ollama runtime, used while the Owner has saved no order of machines; private addresses only.
        public string? Endpoint { get; set; }

        // An operator-confirmed budget for Ollama's own volume, never Jularr's cgroup.
        // 0 means no model downloads are allowed. Can be set only after checking the runtime.
        public long OperatorVerifiedFreeBytes { get; set; }
    }

    internal sealed class LocalTranslationGate
    {
        public SemaphoreSlim HeavyWork { get; } = new(1, 1);
    }

    // The Ollama machines in the Owner's order (Admin, `InstanceSettings.TranslationMachines`), else the configured endpoint alone. The first
    // that answers with an approved model translates; one that fails rests a minute, so the next takes over and the first returns by itself.
    internal sealed class LocalTranslationMachines(OllamaLocalModels models, IOptions<LocalTranslationOptions> options, TimeProvider time)
    {
        private static readonly TimeSpan Rest = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Answer = TimeSpan.FromSeconds(3);
        private readonly ConcurrentDictionary<Uri, DateTimeOffset> _resting = new();

        public static async Task<string[]> SavedAsync(LogicContext ctx, CancellationToken ct) =>
            (await ctx.ReadSql.ReadOptionalAsync<TranslationMachinesV1>(new SqlStatement
            {
                Statement = """SELECT "TranslationMachines" AS "Machines" FROM "InstanceSettings" WHERE "Id" = 1;"""
            }, ct))?.Machines ?? [];

        // The free space the Owner typed on the translation page, else the configured value; 0 means no model download is allowed.
        public static async Task<long> FreeBytesAsync(LogicContext ctx, long configured, CancellationToken ct)
        {
            var saved = (await ctx.ReadSql.ReadOptionalAsync<TranslationFreeBytesV1>(new SqlStatement
            {
                Statement = """SELECT "TranslationFreeBytes" AS "FreeBytes" FROM "InstanceSettings" WHERE "Id" = 1;"""
            }, ct))?.FreeBytes ?? 0;
            return saved > 0 ? saved : configured;
        }

        public Uri[] Order(string[] saved) =>
            [.. (saved.Length > 0 ? saved : [options.Value.Endpoint]).Select(OllamaLocalModels.Endpoint).OfType<Uri>().Distinct()];

        // The machine that translates now with this model, or with the first approved one it has.
        public async Task<(Uri Server, string Model)?> ChooseAsync(string[] saved, CancellationToken ct, string? model = null)
        {
            foreach (var server in Order(saved))
            {
                if (_resting.TryGetValue(server, out var until) && until > time.GetUtcNow()) continue;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(Answer);
                    var installed = await models.InstalledAsync(server, timeout.Token);
                    var chosen = model is null ? OllamaLocalModels.Candidates.Reverse().FirstOrDefault(candidate => installed.Contains(candidate, StringComparer.Ordinal))
                        : installed.Contains(model, StringComparer.Ordinal) ? model : null;
                    if (chosen is not null) return (server, chosen);
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException
                    || exception is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    Fail(server);
                }
            }
            return null;
        }

        // A machine that did not answer or failed a block rests; the next in the order takes its work.
        public void Fail(Uri server) => _resting[server] = time.GetUtcNow() + Rest;
    }

    // Persist only the explicitly requested model and its last observed status, never
    // credentials or Ollama responses. The translation cache directory is already a
    // durable /data mount in production. No schema or privileged host access is needed.
    internal sealed class LocalTranslationDownloads : BackgroundService
    {
        private sealed record DownloadSnapshot(string Model, string State, int Progress, string? Machine = null);

        private readonly OllamaLocalModels _models;
        private readonly IOptions<LocalTranslationOptions> _settings;
        private readonly LocalTranslationGate _gate;
        private readonly string _statePath;
        private readonly Channel<(string Model, Uri Server)> _queue = Channel.CreateBounded<(string Model, Uri Server)>(new BoundedChannelOptions(1)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });
        private readonly object _guard = new();
        private string? _active;
        private string _lastModel = "";
        private string _machine = "";
        private string _state = "idle";
        private int _progress;
        private bool _storageHealthy = true;

        public LocalTranslationDownloads(OllamaLocalModels models, IOptions<LocalTranslationOptions> settings,
            IOptions<MediaOptions> media, LocalTranslationGate gate)
        {
            _models = models;
            _settings = settings;
            _gate = gate;
            _statePath = Path.Combine(media.Value.TranslationPath, "ollama-pull-state.json");
            RestorePreviousStatus();
        }

        public (string Model, string State, int Progress, string Machine) Current()
        {
            lock (_guard) return (_active ?? _lastModel, _state, _progress, _machine);
        }

        // The model is downloaded onto the given machine of the order.
        public bool Queue(string model, Uri server, long freeBytes)
        {
            if (!OllamaLocalModels.Supported(model))
                return false;
            // The runtime has its own persistent model volume: do not guess available space
            // from the Jularr container. Every retry must still satisfy the verified budget.
            var size = model switch
            {
                "translategemma:4b" => 3_300_000_000L,
                "translategemma:12b" => 8_100_000_000L,
                "translategemma:27b" => 17_000_000_000L,
                _ => long.MaxValue
            };
            if (freeBytes < size + 2_000_000_000L) return false;
            lock (_guard)
            {
                if (_active is not null || !_storageHealthy) return false;
                var previous = new DownloadSnapshot(_lastModel, _state, _progress, _machine);
                // Persist the opt-in before queuing work; never start a download whose
                // authorization cannot survive a crash or container restart.
                if (!SaveStatus(new DownloadSnapshot(model, "queued", 0, server.ToString()))) return false;
                _active = model;
                _lastModel = model;
                _machine = server.ToString();
                _state = "queued";
                _progress = 0;
                if (_queue.Writer.TryWrite((model, server))) return true;
                _active = null;
                _lastModel = previous.Model;
                _machine = previous.Machine ?? "";
                _state = previous.State;
                _progress = previous.Progress;
                SaveStatus(previous);
                return false;
            }
        }

        private void RestorePreviousStatus()
        {
            try
            {
                if (!File.Exists(_statePath)) return;
                var info = new FileInfo(_statePath);
                if (info.Length is < 2 or > 4096) throw new InvalidDataException("Invalid local download status size.");
                var saved = global::System.Text.Json.JsonSerializer.Deserialize<DownloadSnapshot>(File.ReadAllText(_statePath));
                if (saved is null || !OllamaLocalModels.Supported(saved.Model)
                    || saved.State is not ("queued" or "downloading" or "installed" or "failed" or "interrupted")
                    || saved.Progress is < 0 or > 100) throw new InvalidDataException("Invalid local download status.");
                _lastModel = saved.Model;
                _machine = saved.Machine ?? "";
                _progress = saved.Progress;
                // A request may have been interrupted by a crash. Report that fact,
                // but never silently restart a multi-GB model download on host boot.
                // The Admin can explicitly retry; Ollama itself reuses partial blobs.
                _state = saved.State is "queued" or "downloading" ? "interrupted" : saved.State;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or global::System.Text.Json.JsonException or InvalidDataException)
            {
                _state = "failed";
                _storageHealthy = false;
            }
        }

        // Called under _guard. Write a temporary file and replace atomically so a crash
        // cannot publish half a JSON document. A broken data volume fails closed.
        private bool SaveStatus(DownloadSnapshot status)
        {
            var temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
                File.WriteAllText(temporary, global::System.Text.Json.JsonSerializer.Serialize(status));
                File.Move(temporary, _statePath, overwrite: true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _storageHealthy = false;
                _state = "failed";
                return false;
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Do not auto-resume on reboot: work only starts from an explicit admin pull.
            await foreach (var (model, server) in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await _gate.HeavyWork.WaitAsync(stoppingToken);
                    try
                    {
                        if (!Set("downloading", 0)) throw new IOException("Model status storage is unavailable.");
                        await _models.PullAsync(server, model, stoppingToken, fraction =>
                        {
                            if (!Set("downloading", fraction)) throw new IOException("Model status storage is unavailable.");
                        });
                        if (!Set("installed", 100)) throw new IOException("Model status storage is unavailable.");
                    }
                    finally { _gate.HeavyWork.Release(); }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    Set("interrupted", _progress);
                    break;
                }
                catch (Exception)
                {
                    Set("failed", _progress);
                }
                finally { lock (_guard) _active = null; }
            }
        }

        private bool Set(string state, int progress)
        {
            lock (_guard)
            {
                if (!_storageHealthy) { _state = "failed"; return false; }
                var bounded = Math.Clamp(progress, 0, 100);
                if (_state == state && _progress == bounded) return true;
                if (!SaveStatus(new DownloadSnapshot(_active ?? _lastModel, state, bounded, _machine))) return false;
                _state = state;
                _progress = bounded;
                return true;
            }
        }
    }
}
