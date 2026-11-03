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
        private readonly CodexCli _cli;
        private readonly object _gate = new();
        private CodexDeviceLogin? _login;
        private CodexDeviceCode? _code;
        private string? _errorKey;
        private CodexCatalog? _catalog;
        private DateTimeOffset _catalogAt;
        private DateTimeOffset _catalogFailedAt;

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
