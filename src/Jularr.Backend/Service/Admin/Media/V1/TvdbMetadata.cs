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
    // A TVDB session lives in memory, with its project key encrypted at rest.
    internal sealed class TvdbMetadata(TvdbClient client, IDataProtectionProvider protection)
    {
        private readonly IDataProtector _protector = protection.CreateProtector(SaveProviderSecretV1.ProtectorPurpose);

        public async Task<TvdbSession> ConnectAsync(LogicContext context, CancellationToken ct) =>
            await LoginAsync(await context.ReadProviderCredentialAsync(new ProviderParameters("tvdb"), ct), ct);

        // For callers between batches: the key is read in its own short batch.
        public async Task<TvdbSession> OpenAsync(LogicContext context, CancellationToken ct) =>
            await LoginAsync(await context.ExecuteBatchAsync((ctx, token) => ctx.ReadProviderCredentialAsync(new ProviderParameters("tvdb"), token), ct, mode: SqlAccessMode.ReadOnly), ct);

        public async Task<TvdbSession> LoginAsync(ProviderCredentialData? credential, CancellationToken ct)
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
            catch (ArgumentException)
            {
                throw new DataValidationException("ImdbId", "invalid_type");
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

        // The default order without a language; with one, its translated names and summaries, none when TheTVDB has no translation in that language.
        public async Task<IReadOnlyList<TvdbEpisode>> ReadEpisodesAsync(TvdbSession session, int tvdbId, string? language, CancellationToken ct)
        {
            try
            {
                return await client.ReadEpisodesAsync(session, tvdbId, language, ct);
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
}
