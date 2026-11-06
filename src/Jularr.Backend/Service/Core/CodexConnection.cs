using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Admin.Ai.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Integrations.Data.Codex;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Logic.Codex;

namespace Jularr.Backend.Service.Core
{
    // The server's one Codex connection: the CLI's stored sign-in and at most one device login that waits for the person.
    public sealed class CodexConnection : IDisposable
    {
        private static readonly TimeSpan CodeTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan CatalogAge = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan CatalogRetry = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ConnectedAge = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RunResume = TimeSpan.FromMinutes(15);
        // The chapter whose requests this flow makes, if any.
        private static readonly AsyncLocal<CodexRun?> CurrentRun = new();
        private readonly CodexCli _cli;
        private readonly object _gate = new();
        private CodexDeviceLogin? _login;
        private CodexDeviceCode? _code;
        private string? _errorKey;
        private CodexCatalog? _catalog;
        private DateTimeOffset _catalogAt;
        private DateTimeOffset _catalogFailedAt;
        private string[] _knownModels = [];
        private bool _warming;
        private bool _connected;
        private DateTimeOffset _connectedAt;
        private DateOnly _usageDay;
        private long _usedToday;
        private long _usedSinceStart;
        private bool _usageLoaded;
        private readonly SortedDictionary<string, long> _history = [];
        private readonly List<CodexRequest> _requests = [];
        private readonly List<CodexRun> _runs = [];
        private DateTimeOffset _planLimitedUntil;

        internal CodexConnection(CodexCli cli)
        {
            _cli = cli;
        }

        public async Task<CodexConnectionV1> ReadAsync(CancellationToken ct)
        {
            // A login that waits for the person is the state; no CLI call runs beside it.
            lock (_gate)
            {
                if (_login is not null && _code is not null)
                {
                    return new CodexConnectionV1(CodexPhase.LoginPending, null, null, _code.VerificationUrl.AbsoluteUri, _code.UserCode, null);
                }
            }

            var status = await _cli.ReadStatusAsync(ct);
            string? errorKey;
            lock (_gate)
            {
                errorKey = _errorKey;
            }

            return !status.Installed ? new CodexConnectionV1(CodexPhase.NotInstalled, null, null, null, null, null)
                : status.Connected ? new CodexConnectionV1(CodexPhase.Connected, status.Version, status.SignIn, null, null, null)
                : new CodexConnectionV1(CodexPhase.NotConnected, status.Version, null, null, null, errorKey);
        }

        // The account's models and plan usage, asked from the app-server at most once a minute; null when Codex could not be asked.
        internal async Task<CodexCatalog?> ReadCatalogAsync(CancellationToken ct)
        {
            lock (_gate)
            {
                if (_catalog is not null && DateTimeOffset.UtcNow - _catalogAt < CatalogAge) return _catalog;
                if (DateTimeOffset.UtcNow - _catalogFailedAt < CatalogRetry) return null;
            }

            try
            {
                var catalog = await _cli.ReadCatalogAsync(ct);
                lock (_gate)
                {
                    _catalog = catalog;
                    _knownModels = [.. catalog.Models.Select(model => model.Id)];
                    _catalogAt = DateTimeOffset.UtcNow;
                    _catalogFailedAt = default;
                }

                return catalog;
            }
            catch (ProviderUnavailableException)
            {
                // A Codex that cannot answer is not asked again at every page view.
                lock (_gate)
                {
                    _catalogFailedAt = DateTimeOffset.UtcNow;
                }

                return null;
            }
        }

        // The model ids the account offered when last asked (kept after the cache ran out); empty until it was asked once.
        internal string[] KnownModels
        {
            get
            {
                lock (_gate)
                {
                    return _knownModels;
                }
            }
        }

        // Asks for the models in the background when the list is empty or old, so a model id the Owner saved is known to the translation without a page visit.
        internal void WarmModels()
        {
            lock (_gate)
            {
                if (_warming || _knownModels.Length > 0 && DateTimeOffset.UtcNow - _catalogAt < TimeSpan.FromMinutes(10)) return;
                _warming = true;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await ReadCatalogAsync(default);
                }
                finally
                {
                    lock (_gate)
                    {
                        _warming = false;
                    }
                }
            });
        }

        // Whether a sign-in is stored; asked from the CLI at most every thirty seconds.
        internal async Task<bool> IsConnectedAsync(CancellationToken ct)
        {
            lock (_gate)
            {
                if (DateTimeOffset.UtcNow - _connectedAt < ConnectedAge) return _connected;
            }

            var connected = (await _cli.ReadStatusAsync(ct)).Connected;
            lock (_gate)
            {
                _connected = connected;
                _connectedAt = DateTimeOffset.UtcNow;
            }

            return connected;
        }

        // The tokens Codex used today (UTC) and since Jularr started; the daily count survives a restart.
        internal (long Today, long SinceStart) Usage()
        {
            lock (_gate)
            {
                LoadUsage();
                return (_usedToday, _usedSinceStart);
            }
        }

        // True when a limit above 0 is used up.
        internal bool LimitReached(long dailyTokens, long runTokens)
        {
            var (today, sinceStart) = Usage();
            return dailyTokens > 0 && today >= dailyTokens || runTokens > 0 && sinceStart >= runTokens;
        }

        // One prompt to the chosen model; its tokens are counted against the limits whether or not the answer is used. `what` names the request on the AI page.
        internal Task<string> ExecAsync(string model, string? effort, string prompt, string what, CancellationToken ct) =>
            AskAsync(model, effort, what, token => _cli.ExecAsync(model, effort, prompt, token), ct);

        // The same in a bare thread of the app-server, the answer's text handed to `onText` as it arrives; the request costs a fraction of an exec request.
        internal Task<string> StreamAsync(string model, string? effort, string prompt, string what, Action<string> onText, CancellationToken ct) =>
            AskAsync(model, effort, what, token => _cli.StreamAsync(model, effort, prompt, onText, token), ct);

        private async Task<string> AskAsync(string model, string? effort, string what, Func<CancellationToken, Task<CodexAnswer>> ask, CancellationToken ct)
        {
            if (!CodexSettings.IsModelName(model) || effort is not null && !CodexSettings.IsEffortName(effort)) throw new InvalidDataException("Codex model or effort is not valid.");
            var request = new CodexRequest(what, model, effort, DateTimeOffset.UtcNow);
            var run = CurrentRun.Value;
            lock (_gate)
            {
                _requests.Add(request);
                Trim(_requests, item => item.State != CodexRequestState.Running, item => item.Started);
            }

            try
            {
                var answer = await ask(ct);
                Record(answer.Tokens);
                lock (_gate)
                {
                    _planLimitedUntil = default;
                    request.Finish(CodexRequestState.Done, answer.Tokens);
                    run?.Add(answer.Tokens);
                }

                return answer.Text;
            }
            catch (CodexPlanLimitException limit)
            {
                lock (_gate)
                {
                    // Codex names the time it is usable again; without one, and at most, the limit is assumed to last a short while or a day.
                    var now = DateTimeOffset.UtcNow;
                    _planLimitedUntil = limit.Until is { } until && until > now ? (until < now.AddDays(1) ? until : now.AddDays(1)) : now.AddMinutes(30);
                    request.Finish(CodexRequestState.PlanLimit, 0);
                    run?.Fail(CodexRequestState.PlanLimit);
                }

                throw new InvalidDataException("The Codex usage limit is reached.", limit);
            }
            catch (ProviderUnavailableException)
            {
                lock (_gate)
                {
                    request.Finish(CodexRequestState.Failed, 0);
                    run?.Fail(CodexRequestState.Failed);
                }

                throw new InvalidDataException("Codex is not available.");
            }
            catch
            {
                lock (_gate)
                {
                    request.Finish(CodexRequestState.Failed, 0);
                    run?.Fail(CodexRequestState.Failed);
                }

                throw;
            }
        }

        // One chapter translated in whole-passage requests: until it is disposed, every request of this flow counts toward it, so the AI page shows what the
        // chapter cost with that model and effort. A chapter that asked Codex nothing is not listed.
        internal IDisposable BeginRun(int chapter, string? title, string model, string? effort)
        {
            CodexRun run;
            lock (_gate)
            {
                // A chapter's next request, within a quarter of an hour of the last, goes on adding to the same row.
                run = _runs.FirstOrDefault(item => item.Matches(chapter, title, model, effort) && !item.IsRunning && item.EndedWithin(RunResume)) ?? new CodexRun(chapter, title, model, effort, DateTimeOffset.UtcNow);
                if (run.Requests == 0) _runs.Add(run);
                run.Resume();
                Trim(_runs, item => item.State != CodexRequestState.Running, item => item.Started);
            }

            CurrentRun.Value = run;
            return new RunScope(this, run);
        }

        private void EndRun(CodexRun run)
        {
            CurrentRun.Value = null;
            lock (_gate)
            {
                if (run.Requests == 0) _runs.Remove(run);
                else run.End();
            }
        }

        private sealed class RunScope(CodexConnection connection, CodexRun run) : IDisposable
        {
            public void Dispose() => connection.EndRun(run);
        }

        // Called inside the lock: the running ones and the last twenty that ended.
        private static void Trim<T>(List<T> items, Func<T, bool> ended, Func<T, DateTimeOffset> started)
        {
            var finished = items.Where(ended).OrderBy(started).ToList();
            foreach (var old in finished.Take(Math.Max(0, finished.Count - 20))) items.Remove(old);
        }

        // When Codex said its usage limit is lifted; default (past) when it is not limited.
        internal DateTimeOffset? PlanLimitedUntil
        {
            get
            {
                lock (_gate)
                {
                    return _planLimitedUntil > DateTimeOffset.UtcNow ? _planLimitedUntil : null;
                }
            }
        }

        // The running requests and the last twenty that ended, newest first.
        internal CodexRequestV1[] Requests()
        {
            lock (_gate)
            {
                return [.. _requests.OrderByDescending(item => item.Started).Select(item => item.Snapshot())];
            }
        }

        // The running chapters and the last twenty that ended, newest first.
        internal CodexRunV1[] Runs()
        {
            lock (_gate)
            {
                return [.. _runs.OrderByDescending(item => item.Started).Select(item => item.Snapshot())];
            }
        }

        // The tokens per UTC day of the last 30 days, oldest first; today's count included.
        internal (string Day, long Tokens)[] History()
        {
            lock (_gate)
            {
                LoadUsage();
                return [.. _history.TakeLast(30).Select(pair => (pair.Key, pair.Value))];
            }
        }

        private void Record(long tokens)
        {
            if (tokens <= 0) return;
            lock (_gate)
            {
                LoadUsage();
                _usedToday += tokens;
                _usedSinceStart += tokens;
                _history[_usageDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = _usedToday;
                while (_history.Count > 60) _history.Remove(_history.Keys.First());
                try
                {
                    File.WriteAllText(UsagePath(), JsonSerializer.Serialize(new UsageFile(_usageDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), _usedToday, new Dictionary<string, long>(_history))));
                }
                catch (IOException)
                {
                }
            }
        }

        // Called inside the lock: the history and today's count from the file, or a new day.
        private void LoadUsage()
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (_usageLoaded && _usageDay == today) return;
            _usageDay = today;
            _usedToday = 0;
            if (!_usageLoaded)
            {
                try
                {
                    var file = JsonSerializer.Deserialize<UsageFile>(File.ReadAllText(UsagePath()));
                    if (file?.History is not null)
                        foreach (var (day, tokens) in file.History)
                            if (DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && tokens > 0) _history[day] = tokens;
                    if (file is not null && file.Day == today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) _usedToday = Math.Max(0, file.Tokens);
                }
                catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
                {
                }
            }
            else if (_history.TryGetValue(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var known))
            {
                _usedToday = known;
            }

            _usageLoaded = true;
        }

        private string UsagePath()
        {
            Directory.CreateDirectory(_cli.HomePath);
            return Path.Combine(_cli.HomePath, "jularr-usage.json");
        }

        private sealed record UsageFile(string Day, long Tokens, Dictionary<string, long>? History = null);

        public async Task<CodexConnectionV1> StartLoginAsync(CancellationToken ct)
        {
            CodexDeviceLogin login;
            lock (_gate)
            {
                if (_login is not null) throw new ServiceUnavailableException("codex_login_busy");
                try
                {
                    login = _cli.StartDeviceLogin();
                }
                catch (ProviderUnavailableException)
                {
                    throw new ServiceUnavailableException("codex_not_installed");
                }

                _login = login;
                _code = null;
                _errorKey = null;
            }

            // The code comes within seconds; a CLI that ends before it showed one failed.
            CodexDeviceCode? code;
            try
            {
                code = await login.Code.WaitAsync(CodeTimeout, ct);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                login.Cancel();
                Finish(login, "codex_login_failed");
                if (exception is OperationCanceledException) throw;
                throw new ServiceUnavailableException("codex_login_failed");
            }

            if (code is null)
            {
                Finish(login, "codex_login_failed");
                throw new ServiceUnavailableException("codex_login_failed");
            }

            lock (_gate)
            {
                if (ReferenceEquals(_login, login)) _code = code;
            }

            _ = WatchAsync(login);
            return new CodexConnectionV1(CodexPhase.LoginPending, null, null, code.VerificationUrl.AbsoluteUri, code.UserCode, null);
        }

        public void CancelLogin()
        {
            CodexDeviceLogin? login;
            lock (_gate)
            {
                login = _login;
                _login = null;
                _code = null;
                _errorKey = null;
            }

            login?.Cancel();
        }

        public async Task LogoutAsync(CancellationToken ct)
        {
            CancelLogin();
            lock (_gate)
            {
                _catalog = null;
                _connectedAt = default;
            }

            if (!await _cli.LogoutAsync(ct)) throw new ServiceUnavailableException("codex_logout_failed");
        }

        public void Dispose()
        {
            CancelLogin();
        }

        // The login ends by itself: the person signed in, the code expired or the CLI failed.
        private async Task WatchAsync(CodexDeviceLogin login)
        {
            var signedIn = await login.Completed;
            Finish(login, signedIn || login.IsCancelled ? null : "codex_login_expired");
        }

        private void Finish(CodexDeviceLogin login, string? errorKey)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_login, login)) return;
                _catalog = null;
                _catalogFailedAt = default;
                _connectedAt = default;
                _login = null;
                _code = null;
                _errorKey = errorKey;
            }

            login.Dispose();
        }
    }

    // The Owner's saved Codex choices; what translation reads before it runs Codex.
    public static partial class CodexSettings
    {
        internal static async Task<CodexSettingsV1> ReadAsync(LogicContext ctx, CancellationToken ct) =>
            await ctx.ReadSql.ReadOptionalAsync<CodexSettingsV1>(new SqlStatement
            {
                Statement = """
                    SELECT "CodexModel" AS "Model", "CodexEffort" AS "Effort", "CodexFallbackModel" AS "FallbackModel", "CodexDailyTokens" AS "DailyTokens",
                        "CodexRunTokens" AS "RunTokens", "CodexWarnPercent" AS "WarnPercent" FROM "InstanceSettings" WHERE "Id" = 1;
                    """
            }, ct) ?? new CodexSettingsV1(null, null, null, 0, 0, 80);

        public static bool IsModelName(string value) => ModelPattern().IsMatch(value);
        public static bool IsEffortName(string value) => EffortPattern().IsMatch(value);

        [GeneratedRegex("^[A-Za-z0-9._:-]{1,64}$")]
        private static partial Regex ModelPattern();

        [GeneratedRegex("^[a-z]{3,16}$")]
        private static partial Regex EffortPattern();
    }

    internal enum CodexRequestState : byte
    {
        Running = 0,
        Done = 1,
        Failed = 2,
        PlanLimit = 3
    }

    // One request to Codex, as the AI page shows it while it runs and after it ended.
    internal sealed class CodexRequest(string what, string model, string? effort, DateTimeOffset started)
    {
        private DateTimeOffset? _finished;
        private long _tokens;

        public string What { get; } = what;
        public string Model { get; } = model;
        public DateTimeOffset Started { get; } = started;
        public CodexRequestState State { get; private set; }

        public void Finish(CodexRequestState state, long tokens)
        {
            State = state;
            _tokens = tokens;
            _finished = DateTimeOffset.UtcNow;
        }

        public CodexRequestV1 Snapshot() => new(What, Model, effort, Started, (byte)State, _tokens, Math.Round(((_finished ?? DateTimeOffset.UtcNow) - Started).TotalSeconds, 1));
    }

    // One chapter's requests together; changed inside the connection's lock. It ends Done unless a request failed.
    internal sealed class CodexRun(int chapter, string? title, string model, string? effort, DateTimeOffset started)
    {
        private DateTimeOffset? _finished;
        private CodexRequestState _failure;
        private long _tokens;
        private int _open;

        public bool IsRunning => _open > 0;

        public bool Matches(int otherChapter, string? otherTitle, string otherModel, string? otherEffort) =>
            chapter == otherChapter && title == otherTitle && model == otherModel && effort == otherEffort;

        public bool EndedWithin(TimeSpan span) => _finished is { } finished && DateTimeOffset.UtcNow - finished <= span;

        // Another request of this chapter begins; the row is running again, with its earlier failure forgotten.
        public void Resume()
        {
            _open++;
            _finished = null;
            _failure = CodexRequestState.Running;
            State = CodexRequestState.Running;
        }

        public DateTimeOffset Started { get; } = started;
        public CodexRequestState State { get; private set; }
        public int Requests { get; private set; }

        public void Add(long tokens)
        {
            Requests++;
            _tokens += tokens;
        }

        public void Fail(CodexRequestState state)
        {
            Requests++;
            _failure = state;
        }

        public void End()
        {
            if (--_open > 0) return;
            State = _failure == CodexRequestState.Running ? CodexRequestState.Done : _failure;
            _finished = DateTimeOffset.UtcNow;
        }

        public CodexRunV1 Snapshot() => new(chapter, title, model, effort, Started, (byte)State, Requests, _tokens, Math.Round(((_finished ?? DateTimeOffset.UtcNow) - Started).TotalSeconds, 1));
    }
}
