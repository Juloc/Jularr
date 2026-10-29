using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Integrations.Data.Tvdb;
using Jularr.Integrations.Logic.Tvdb;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // A TVDB session lives in memory, with its project key encrypted at rest. One session per key is used for most of the month TheTVDB keeps it, and a session
    // TheTVDB refuses is replaced once; every request counts against the minute budget background work keeps to.
    internal sealed class TvdbMetadata(TvdbClient client, IDataProtectionProvider protection, TvdbConnectionState state)
    {
        private readonly IDataProtector _protector = protection.CreateProtector(SaveProviderSecretV1.ProtectorPurpose);

        public async Task<TvdbSession> ConnectAsync(LogicContext context, CancellationToken ct) =>
            await LoginAsync(await context.ReadProviderCredentialAsync(new ProviderParameters("tvdb"), ct), ct);

        // For callers between batches: the key is read in its own short batch.
        public async Task<TvdbSession> OpenAsync(LogicContext context, CancellationToken ct) =>
            await LoginAsync(await context.ExecuteBatchAsync((ctx, token) => ctx.ReadProviderCredentialAsync(new ProviderParameters("tvdb"), token), ct, mode: SqlAccessMode.ReadOnly), ct);

        public async Task<TvdbSession> LoginAsync(ProviderCredentialData? credential, CancellationToken ct)
        {
            // Without an enabled key there are no series details and no series search.
            if (credential is not { IsEnabled: true, ProtectedSecret: { } secret })
            {
                throw new ServiceUnavailableException("tvdb_key_missing");
            }

            return state.Sessions.TryGetValue(Fingerprint(secret), out var kept) && kept.ExpiresAt > DateTimeOffset.UtcNow ? kept.Session : await LoginAsync(secret, ct);
        }

        private async Task<TvdbSession> LoginAsync(byte[] secret, CancellationToken ct)
        {
            byte[] clear;
            try
            {
                clear = _protector.Unprotect(secret);
            }
            catch (CryptographicException)
            {
                throw new ServiceUnavailableException("metadata_authentication_failed");
            }

            try
            {
                state.Record();
                var session = await client.LoginAsync(Encoding.UTF8.GetString(clear), null, ct);
                state.Sessions[Fingerprint(secret)] = (session, DateTimeOffset.UtcNow + TvdbConnectionState.SessionLifetime);
                state.Secrets.AddOrUpdate(session, secret);
                return session;
            }
            catch (TvdbException exception)
            {
                throw Translate(exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }

        // One TheTVDB request with the key's current session; a session TheTVDB no longer accepts is replaced once and the request asked again.
        private async Task<T> CallAsync<T>(TvdbSession session, Func<TvdbSession, Task<T>> call, CancellationToken ct)
        {
            var known = state.Secrets.TryGetValue(session, out var secret);
            if (known && state.Sessions.TryGetValue(Fingerprint(secret!), out var current))
            {
                session = current.Session;
            }

            try
            {
                state.Record();
                return await call(session);
            }
            catch (TvdbException exception) when (exception.Failure == TvdbFailure.AuthenticationFailed && known)
            {
                state.Sessions.TryRemove(Fingerprint(secret!), out _);
                var renewed = await LoginAsync(secret!, ct);
                state.Record();
                return await call(renewed);
            }
        }

        private static string Fingerprint(byte[] secret) => Convert.ToHexString(SHA256.HashData(secret));

        // Whether background work may still ask TheTVDB this minute for this many requests, within the TheTVDB catalog card's requests a minute.
        public bool HasBudget(int requestsPerMinute, int needed) => state.Remaining(requestsPerMinute) >= needed;

        // Takes one request of this minute's budget; false while it is used up.
        public bool TryTakeBudget(int requestsPerMinute) => state.TryTake(requestsPerMinute);

        public async Task<int?> FindSeriesByImdbAsync(TvdbSession session, string imdbId, CancellationToken ct)
        {
            try
            {
                return await CallAsync(session, current => client.FindSeriesByImdbAsync(current, imdbId, ct), ct);
            }
            catch (ArgumentException)
            {
                throw new DataValidationException("ImdbId", "invalid_type");
            }
            catch (TvdbException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<IReadOnlyList<TvdbSearchResult>> SearchSeriesAsync(TvdbSession session, string query, int? year, int offset, int limit, CancellationToken ct)
        {
            try
            {
                return await CallAsync(session, current => client.SearchSeriesAsync(current, query, year, offset, limit, ct), ct);
            }
            catch (TvdbException exception) when (exception.Failure == TvdbFailure.NotFound)
            {
                return [];
            }
            catch (TvdbException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<TvdbSeries> ReadSeriesAsync(TvdbSession session, int tvdbId, CancellationToken ct)
        {
            try
            {
                return await CallAsync(session, current => client.ReadSeriesAsync(current, tvdbId, ct), ct);
            }
            catch (TvdbException exception)
            {
                throw Translate(exception);
            }
        }

        // A person's IMDb and TMDB ids; none for a person TheTVDB no longer knows. The caller took the request from the budget.
        public async Task<TvdbPerson> ReadPersonAsync(TvdbSession session, int personId, CancellationToken ct)
        {
            var known = state.Secrets.TryGetValue(session, out var secret);
            if (known && state.Sessions.TryGetValue(Fingerprint(secret!), out var current))
            {
                session = current.Session;
            }

            try
            {
                return await client.ReadPersonAsync(session, personId, ct);
            }
            catch (TvdbException exception) when (exception.Failure == TvdbFailure.NotFound)
            {
                return new TvdbPerson(personId, null, null);
            }
            catch (TvdbException exception)
            {
                throw Translate(exception);
            }
        }

        // Episode titles in English like Sonarr, an episode without one keeps its original-language name.
        public async Task<IReadOnlyList<TvdbEpisode>> ReadEpisodesAsync(TvdbSession session, int tvdbId, CancellationToken ct)
        {
            try
            {
                return await CallAsync(session, current => client.ReadEpisodesAsync(current, tvdbId, ct), ct);
            }
            catch (TvdbException exception)
            {
                throw Translate(exception);
            }
        }

        // The default order without a language; with one, its translated names and summaries, none when TheTVDB has no translation in that language.
        public async Task<IReadOnlyList<TvdbEpisode>> ReadEpisodesAsync(TvdbSession session, int tvdbId, string? language, CancellationToken ct)
        {
            try
            {
                return await CallAsync(session, current => client.ReadEpisodesAsync(current, tvdbId, language, ct), ct);
            }
            catch (TvdbException exception) when (language is not null && exception.Failure == TvdbFailure.NotFound)
            {
                return [];
            }
            catch (TvdbException exception)
            {
                throw Translate(exception);
            }
        }

        private static Exception Translate(TvdbException exception) => exception.Failure switch
        {
            TvdbFailure.NotFound => new KeyNotFoundException("TheTVDB does not know this series."),
            TvdbFailure.AuthenticationFailed => new ServiceUnavailableException("metadata_authentication_failed"),
            TvdbFailure.RateLimited => new ServiceUnavailableException("metadata_rate_limited"),
            TvdbFailure.Ambiguous => new DataValidationException("TvdbId", "in_use"),
            _ => new ServiceUnavailableException("metadata_unavailable")
        };
    }

    // What the process keeps of TheTVDB between requests: a session per key and the requests of the last minute.
    internal sealed class TvdbConnectionState
    {
        // TheTVDB's sessions are valid for a month.
        public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(25);
        private static readonly long WindowMilliseconds = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;
        private readonly Queue<long> _requests = new();

        public ConcurrentDictionary<string, (TvdbSession Session, DateTimeOffset ExpiresAt)> Sessions { get; } = new(StringComparer.Ordinal);
        public ConditionalWeakTable<TvdbSession, byte[]> Secrets { get; } = new();

        public void Record()
        {
            lock (_requests)
            {
                Trim();
                _requests.Enqueue(Environment.TickCount64);
            }
        }

        public int Remaining(int requestsPerMinute)
        {
            lock (_requests)
            {
                Trim();
                return Math.Max(requestsPerMinute - _requests.Count, 0);
            }
        }

        public bool TryTake(int requestsPerMinute)
        {
            lock (_requests)
            {
                Trim();
                if (_requests.Count >= requestsPerMinute) return false;
                _requests.Enqueue(Environment.TickCount64);
                return true;
            }
        }

        private void Trim()
        {
            var since = Environment.TickCount64 - WindowMilliseconds;
            while (_requests.TryPeek(out var oldest) && oldest <= since) _requests.Dequeue();
        }
    }
}
