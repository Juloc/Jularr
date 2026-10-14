using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Integrations.Data.Igdb;
using Jularr.Integrations.Logic.Igdb;
using Microsoft.AspNetCore.DataProtection;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // IGDB is the canonical games provider; its platforms become Jularr's own platform identities.
    internal sealed class GameMetadata
    {
        public const string Namespace = "igdb";
        private readonly IgdbClient _igdb;
        private readonly IDataProtector _protector;

        public GameMetadata(IgdbClient igdb, IDataProtectionProvider protection)
        {
            _igdb = igdb;
            _protector = protection.CreateProtector(SaveProviderSecretV1.ProtectorPurpose);
        }

        public static bool Handles(MediaType mediaType) => mediaType == MediaType.Game;

        public async Task<IgdbConnection> ConnectAsync(LogicContext context, CancellationToken ct)
        {
            // The Twitch application's id and secret both belong to the provider registry.
            var credential = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadProviderCredentialAsync(new ProviderParameters(Namespace), token), ct);
            if (credential is not { IsEnabled: true, ProtectedSecret: { } secret, ExternalId: { } clientId })
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
                throw new ServiceUnavailableException("metadata_unavailable");
            }

            try
            {
                return new IgdbConnection(clientId, Encoding.UTF8.GetString(clear));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }

        public async Task<IReadOnlyList<IgdbGame>> SearchAsync(IgdbConnection connection, string query, int? year, CancellationToken ct)
        {
            try
            {
                var results = await _igdb.SearchAsync(connection, query, ct);
                return year is { } wanted ? [.. results.OrderByDescending(result => result.Year == wanted)] : results;
            }
            catch (IgdbException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<long> ImportAsync(LogicContext context, IgdbConnection connection, string id, CancellationToken ct)
        {
            // Read before the short write batch.
            IgdbGame game;
            try
            {
                game = await _igdb.ReadAsync(connection, int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var gameId) ? gameId : throw new KeyNotFoundException("Invalid IGDB id."), ct);
            }
            catch (IgdbException exception)
            {
                throw Translate(exception);
            }

            var facts = new[]
            {
                new { Key = "Developer", Value = TextLimit.Cut(string.Join(", ", game.Developers), 4000), Localized = false },
                new { Key = "Overview", Value = TextLimit.Cut(game.Summary, 4000), Localized = false },
                new { Key = "Poster", Value = game.CoverImageId is { } cover ? IgdbClient.CoverUrl(cover) : null, Localized = false }
            }.Where(fact => !string.IsNullOrWhiteSpace(fact.Value));
            var data = new WorkMetadataData(MediaType.Game, null, Namespace, game.Id.ToString(CultureInfo.InvariantCulture), game.Name, game.AlternativeNames.FirstOrDefault(),
                game.Year is >= 1950 and <= 2200 ? game.Year : null, "[]", "[]", JsonSerializer.Serialize(facts), Namespace);
            var platforms = JsonSerializer.Serialize(game.Platforms.Select(platform => new { platform.Id, platform.Name, platform.Abbreviation }));
            return await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                var workId = await ctx.SaveWorkMetadataAsync(data, token);
                await ctx.SaveWorkPlatformsAsync(new WorkPlatformsData(workId, platforms), token);
                return workId;
            }, ct);
        }

        private static Exception Translate(IgdbException exception) => exception.Failure switch
        {
            IgdbFailure.NotFound => new KeyNotFoundException("IGDB does not know this game."),
            IgdbFailure.RateLimited => new ServiceUnavailableException("metadata_rate_limited"),
            _ => new ServiceUnavailableException("metadata_unavailable")
        };
    }
}
