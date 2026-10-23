using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Integrations.Data.Tvdb;
using Jularr.Integrations.Logic.Tvdb;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // A TVDB session lives in memory, with its project key encrypted at rest.
    internal sealed class TvdbMetadata(TvdbClient client, IDataProtectionProvider protection)
    {
        private readonly IDataProtector _protector = protection.CreateProtector(SaveProviderSecretV1.ProtectorPurpose);

        public async Task<TvdbSession> ConnectAsync(LogicContext context, CancellationToken ct) =>
            await LoginAsync(await context.ReadProviderCredentialAsync(new ProviderParameters("tvdb"), ct), ct);

        private async Task<TvdbSession> LoginAsync(ProviderCredentialData? credential, CancellationToken ct)
        {
            if (credential is not { IsEnabled: true, ProtectedSecret: { } secret })
            {
                throw new ServiceUnavailableException("metadata_unavailable");
            }

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
                return await client.LoginAsync(Encoding.UTF8.GetString(clear), null, ct);
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

        public async Task<int?> FindSeriesByImdbAsync(TvdbSession session, string imdbId, CancellationToken ct)
        {
            try
            {
                return await client.FindSeriesByImdbAsync(session, imdbId, ct);
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
                return await client.ReadSeriesAsync(session, tvdbId, ct);
            }
            catch (TvdbException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<IReadOnlyList<TvdbEpisode>> ReadEpisodesAsync(TvdbSession session, int tvdbId, CancellationToken ct)
        {
            try
            {
                return await client.ReadEpisodesAsync(session, tvdbId, ct);
            }
            catch (TvdbException exception)
            {
                throw Translate(exception);
            }
        }

        // The seasons and episodes TheTVDB names for a series found by its TVDB id or, failing that, its IMDb id; none when TVDB is not set up, does not know
        // the series, disagrees with its IMDb id or is unavailable, so the caller keeps what it has.
        public async Task<TvdbStructure?> TryReadStructureAsync(LogicContext context, int? tvdbId, string? imdbId, CancellationToken ct)
        {
            if (tvdbId is null && imdbId is null)
            {
                return null;
            }

            try
            {
                var credential = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadProviderCredentialAsync(new ProviderParameters("tvdb"), token), ct);
                var session = await LoginAsync(credential, ct);
                var id = tvdbId ?? await FindSeriesByImdbAsync(session, imdbId!, ct);
                if (id is null)
                {
                    return null;
                }

                var series = await ReadSeriesAsync(session, id.Value, ct);
                if (imdbId is not null && series.ImdbId is not null && !string.Equals(imdbId, series.ImdbId, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var episodes = await ReadEpisodesAsync(session, id.Value, ct);
                return episodes.Count == 0 || episodes.GroupBy(episode => (episode.SeasonNumber, episode.EpisodeNumber)).Any(group => group.Count() != 1)
                    ? null
                    : new TvdbStructure(id.Value, episodes);
            }
            catch (Exception exception) when (exception is ServiceUnavailableException or KeyNotFoundException or DataValidationException)
            {
                return null;
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

    internal sealed record TvdbStructure(int TvdbId, IReadOnlyList<TvdbEpisode> Episodes);
}
