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

    // Who translates now: one lane per provider (its Key). Every Ollama machine shares one, so only one machine translates at a time, a failover included;
    // Codex has its own and translates while Ollama does. A model download takes no lane. A person waiting (the Reader, subtitles, the Owner) goes before
    // background work: the book job waits behind everyone, gives up after its short wait, and the lane is handed to the next person first.
    internal sealed class TranslationLanes(TimeProvider time)
    {
        private readonly ConcurrentDictionary<string, Lane> _lanes = new(StringComparer.Ordinal);

        // How long a chunk of a chapter waits for the lane, held by another translation for one request at most, before the Reader is told it is busy.
        public TimeSpan ChunkWait { get; set; } = TimeSpan.FromSeconds(30);

        // The lane once it is free within the wait, else null (at once without a wait); disposing the result hands it on. Cancelling throws.
        public async Task<IDisposable?> TryEnterAsync(string key, TimeSpan wait, CancellationToken ct, bool background = false)
        {
            ct.ThrowIfCancellationRequested();
            var lane = _lanes.GetOrAdd(key, _ => new Lane());
            TaskCompletionSource<bool> turn;
            LinkedListNode<TaskCompletionSource<bool>> waiting;
            lock (lane.Guard)
            {
                if (!lane.Held)
                {
                    lane.Held = true;
                    return new Hold(lane);
                }

                if (wait <= TimeSpan.Zero) return null;
                turn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiting = (background ? lane.Background : lane.People).AddLast(turn);
            }

            // Given up after the wait or on cancel, unless the lane was handed over first.
            using var timeout = new CancellationTokenSource(wait, time);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using (stop.Token.Register(() =>
            {
                lock (lane.Guard)
                {
                    if (waiting.List is null) return;
                    waiting.List.Remove(waiting);
                    turn.TrySetResult(false);
                }
            }))
            {
                if (await turn.Task) return new Hold(lane);
            }

            ct.ThrowIfCancellationRequested();
            return null;
        }

        private sealed class Lane
        {
            public readonly Lock Guard = new();
            public readonly LinkedList<TaskCompletionSource<bool>> People = new();
            public readonly LinkedList<TaskCompletionSource<bool>> Background = new();
            public bool Held;
        }

        private sealed class Hold(Lane lane) : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 1) return;
                lock (lane.Guard)
                {
                    var next = lane.People.First ?? lane.Background.First;
                    if (next is null)
                    {
                        lane.Held = false;
                        return;
                    }

                    next.List!.Remove(next);
                    next.Value.TrySetResult(true);
                }
            }
        }
    }

    // The Ollama machines in the Owner's order (Admin, `InstanceSettings.TranslationMachines`), else the configured endpoint alone. A check asks every one of them
    // outside any transaction (the translationMachines job every thirty seconds, Check now on the translation page) and keeps what each answered; choices, the
    // Reader and the pages read only that. The first that answered with an approved model translates; one that fails a block rests a minute, so the next takes over.
    internal sealed class LocalTranslationMachines(OllamaLocalModels models, IOptions<LocalTranslationOptions> options, TimeProvider time)
    {
        private static readonly TimeSpan Rest = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Answer = TimeSpan.FromSeconds(3);
        private readonly ConcurrentDictionary<Uri, DateTimeOffset> _resting = new();
        private readonly ConcurrentDictionary<Uri, (DateTimeOffset At, string Reason)> _lastFailure = new();
        private readonly ConcurrentDictionary<Uri, TranslationMachineCheck> _checks = new();

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

        // Asks the machines of the order at once for their models and the ones loaded now, each within a few seconds; with onlyNew only those never asked yet.
        // One that does not answer is kept as unreachable with its reason and does not rest: it counts again with the first check it answers.
        public async Task CheckAsync(string[] saved, CancellationToken ct, bool onlyNew = false)
        {
            await Task.WhenAll(Order(saved).Where(server => !onlyNew || !_checks.ContainsKey(server)).Select(async server =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(Answer);
                try
                {
                    var installed = models.InstalledAsync(server, timeout.Token);
                    var running = models.RunningAsync(server, timeout.Token);
                    await Task.WhenAll(installed, running);
                    _checks[server] = new TranslationMachineCheck(time.GetUtcNow(), await installed, await running, null);
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException
                    || exception is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    var reason = ReasonOf(exception);
                    _checks[server] = new TranslationMachineCheck(time.GetUtcNow(), null, [], reason);
                    _lastFailure[server] = (time.GetUtcNow(), reason);
                }
            }));
        }

        // What the machine answered at its last check; null before its first.
        public TranslationMachineCheck? CheckOf(Uri server) => _checks.GetValueOrDefault(server);

        // The machine that translates now with this model, or with the largest approved one it has, by the last check: the first of the order that
        // answered with it and does not rest.
        public (Uri Server, string Model)? Choose(string[] saved, string? model = null)
        {
            foreach (var server in Order(saved))
            {
                if (IsResting(server) || CheckOf(server)?.Installed is not { } installed) continue;
                var chosen = model is null ? OllamaLocalModels.Candidates.Reverse().FirstOrDefault(installed.ContainsKey) : installed.ContainsKey(model) ? model : null;
                if (chosen is not null) return (server, chosen);
            }

            return null;
        }

        // The same for a translation about to run: a machine never asked yet (just after the start or a new order) is asked first.
        public async Task<(Uri Server, string Model)?> ChooseAsync(string[] saved, CancellationToken ct, string? model = null)
        {
            await CheckAsync(saved, ct, onlyNew: true);
            return Choose(saved, model);
        }

        // Every approved model the machines that answered their last check and do not rest have installed.
        public string[] Installed(string[] saved) =>
            [.. Order(saved).Where(server => !IsResting(server)).SelectMany(server => CheckOf(server)?.Installed?.Keys ?? []).Distinct(StringComparer.Ordinal)];

        // The version of the model on the machine by its last check; a translation counts as current only while it matches.
        public string Digest(Uri server, string model) =>
            CheckOf(server)?.Installed?.GetValueOrDefault(model) ?? throw new InvalidDataException("Model digest unavailable; refusing unversioned inference.");

        // A machine that failed a block rests; the next in the order takes its work. The reason is what the Owner reads as its last error.
        public void Fail(Uri server, string reason = "unreachable")
        {
            _resting[server] = time.GetUtcNow() + Rest;
            _lastFailure[server] = (time.GetUtcNow(), reason);
        }

        // Until when the machine rests after a failure, and what failed last (the reason and when); both empty for a machine that never failed since the start.
        public (DateTimeOffset? RestingUntil, string? Reason, DateTimeOffset? FailedAt) StateOf(Uri server)
        {
            var resting = _resting.TryGetValue(server, out var until) && until > time.GetUtcNow() ? until : (DateTimeOffset?)null;
            return _lastFailure.TryGetValue(server, out var failure) ? (resting, failure.Reason, failure.At) : (resting, null, null);
        }

        // Why a question to a machine failed, as a key of the page's texts.
        public static string ReasonOf(Exception exception) => exception switch
        {
            OperationCanceledException => "timeout",
            HttpRequestException => "unreachable",
            _ => "invalid_answer"
        };

        // Machines are set up, and none of them answered its last check without resting since.
        public bool AllAway(string[] saved) => Order(saved) is { Length: > 0 } order && order.All(server => IsResting(server) || CheckOf(server)?.Installed is null);

        private bool IsResting(Uri server) => _resting.TryGetValue(server, out var until) && until > time.GetUtcNow();
    }

    // What a machine answered at its last check: its approved models with the digest of their version and those loaded now, or why it did not answer
    // (Installed is null then).
    internal sealed record TranslationMachineCheck(DateTimeOffset CheckedAt, IReadOnlyDictionary<string, string?>? Installed, string[] Running, string? Reason);

    // Persist only the explicitly requested model and its last observed status, never
    // credentials or Ollama responses. The translation cache directory is already a
    // durable /data mount in production. No schema or privileged host access is needed.
    internal sealed class LocalTranslationDownloads : BackgroundService
    {
        private sealed record DownloadSnapshot(string Model, string State, int Progress, string? Machine = null);

        private readonly OllamaLocalModels _models;
        private readonly IOptions<LocalTranslationOptions> _settings;
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

        public LocalTranslationDownloads(OllamaLocalModels models, IOptions<LocalTranslationOptions> settings, IOptions<MediaOptions> media)
        {
            _models = models;
            _settings = settings;
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
            // Do not auto-resume on reboot: work only starts from an explicit admin pull. One download at a time, beside any translation.
            await foreach (var (model, server) in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    if (!Set("downloading", 0)) throw new IOException("Model status storage is unavailable.");
                    await _models.PullAsync(server, model, stoppingToken, fraction =>
                    {
                        if (!Set("downloading", fraction)) throw new IOException("Model status storage is unavailable.");
                    });
                    if (!Set("installed", 100)) throw new IOException("Model status storage is unavailable.");
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
