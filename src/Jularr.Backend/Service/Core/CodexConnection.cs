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

        // One prompt to the chosen model; its tokens are counted against the limits whether or not the answer is used.
        internal async Task<string> ExecAsync(string model, string? effort, string prompt, CancellationToken ct)
        {
            if (!CodexSettings.IsModelName(model) || effort is not null && !CodexSettings.IsEffortName(effort)) throw new InvalidDataException("Codex model or effort is not valid.");
            try
            {
                var answer = await _cli.ExecAsync(model, effort, prompt, ct);
                Record(answer.Tokens);
                return answer.Text;
            }
            catch (ProviderUnavailableException)
            {
                throw new InvalidDataException("Codex is not available.");
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
                try
                {
                    File.WriteAllText(UsagePath(), JsonSerializer.Serialize(new UsageFile(_usageDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), _usedToday)));
                }
                catch (IOException)
                {
                }
            }
        }

        // Called inside the lock: today's count from the file, or a new day.
        private void LoadUsage()
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (_usageLoaded && _usageDay == today) return;
            _usageDay = today;
            _usedToday = 0;
            _usageLoaded = true;
            try
            {
                var file = JsonSerializer.Deserialize<UsageFile>(File.ReadAllText(UsagePath()));
                if (file is not null && file.Day == today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) _usedToday = Math.Max(0, file.Tokens);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        private string UsagePath()
        {
            Directory.CreateDirectory(_cli.HomePath);
            return Path.Combine(_cli.HomePath, "jularr-usage.json");
        }

        private sealed record UsageFile(string Day, long Tokens);

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
}
