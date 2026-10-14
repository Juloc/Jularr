using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Provider;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Provider;
using Jularr.Integrations.Data.MediaServer;
using Jularr.Integrations.Logic.MediaServer;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Provider.V1
{
    // Connected Jellyfin and Emby servers; shared by Admin and the System sync.
    internal sealed class MediaServers
    {
        private const long MaximumPositionMs = 864_000_000;
        private readonly MediaServerClient _client;
        private readonly IDataProtector _protector;

        public MediaServers(MediaServerClient client, IDataProtectionProvider protection)
        {
            _client = client;
            _protector = protection.CreateProtector(SaveProviderSecretV1.ProtectorPurpose);
        }

        public static string ProviderKey(MediaServerType type) => type.ToString().ToLowerInvariant();

        public byte[] Protect(string apiKey) => _protector.Protect(Encoding.UTF8.GetBytes(apiKey));

        // Null when the server is not connected or switched off.
        public async Task<MediaServerConnection?> ConnectAsync(LogicContext context, MediaServerType type, CancellationToken ct)
        {
            var state = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadMediaServerAsync(new ProviderParameters(ProviderKey(type)), token), ct);
            if (state is not { IsEnabled: true, BaseUrl: { } baseUrl, ProtectedSecret: { } secret })
            {
                return null;
            }

            return new MediaServerConnection(new Uri(baseUrl), Unprotect(secret), Dialect(type));
        }

        public static MediaServerDialect Dialect(MediaServerType type) => type == MediaServerType.Jellyfin ? MediaServerDialect.Jellyfin : MediaServerDialect.Emby;

        public async Task<MediaServerInfo> ReadServerAsync(MediaServerConnection connection, CancellationToken ct)
        {
            try
            {
                return await _client.ReadServerAsync(connection, ct);
            }
            catch (MediaServerException exception)
            {
                throw Translate(exception);
            }
        }

        // The server's users; returns how many there are.
        public async Task<int> RefreshUsersAsync(LogicContext context, MediaServerType type, MediaServerConnection connection, CancellationToken ct)
        {
            IReadOnlyList<MediaServerUser> users;
            try
            {
                users = await _client.ReadUsersAsync(connection, ct);
            }
            catch (MediaServerException exception)
            {
                throw Translate(exception);
            }

            var rows = JsonSerializer.Serialize(users.Select(user => new { user.Id, user.Name }));
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.SaveMediaServerUsersAsync(new MediaServerUsersData(ProviderKey(type), rows), token);
                return true;
            }, ct);
            return users.Count;
        }

        // Every confirmed user page by page; an unreachable server leaves the agreed state as it was, so the next pass catches up.
        public async Task<(int Local, int Remote)> SyncAsync(LogicContext context, MediaServerType type, MediaServerConnection connection, CancellationToken ct)
        {
            await RefreshUsersAsync(context, type, connection, ct);
            var key = new ProviderParameters(ProviderKey(type));
            var links = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadSyncLinksAsync(key, token), ct);
            var local = 0;
            var remote = 0;
            try
            {
                foreach (var link in links)
                {
                    var series = await ReadSeriesAsync(connection, link.ExternalUserId, ct);
                    for (var start = 0; ; start += MediaServerClient.PageSize)
                    {
                        var page = await _client.ReadItemsAsync(connection, link.ExternalUserId, false, start, ct);
                        var counts = await SyncPageAsync(context, connection, link, series, page.Items, ct);
                        local += counts.Local;
                        remote += counts.Remote;
                        if (page.Items.Count < MediaServerClient.PageSize || start + page.Items.Count >= page.TotalSize)
                        {
                            break;
                        }
                    }
                }
            }
            catch (MediaServerException exception)
            {
                throw Translate(exception);
            }

            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.CompleteMediaServerSyncAsync(key, token);
                return true;
            }, ct);
            return (local, remote);
        }

        // Series ids by item id; episodes name their series only by id.
        private async Task<Dictionary<string, MediaServerItem>> ReadSeriesAsync(MediaServerConnection connection, string userId, CancellationToken ct)
        {
            var series = new Dictionary<string, MediaServerItem>(StringComparer.Ordinal);
            for (var start = 0; ; start += MediaServerClient.PageSize)
            {
                var page = await _client.ReadItemsAsync(connection, userId, true, start, ct);
                foreach (var item in page.Items)
                {
                    series[item.Id] = item;
                }

                if (page.Items.Count < MediaServerClient.PageSize || start + page.Items.Count >= page.TotalSize)
                {
                    return series;
                }
            }
        }

        private async Task<(int Local, int Remote)> SyncPageAsync(LogicContext context, MediaServerConnection connection, MediaServerSyncLinkData link,
            Dictionary<string, MediaServerItem> series, IReadOnlyList<MediaServerItem> items, CancellationToken ct)
        {
            var rows = items.Select(item =>
            {
                var owner = item.Kind == MediaServerItemKind.Episode ? series.GetValueOrDefault(item.SeriesId ?? "") : item;
                return owner is null || item.Kind == MediaServerItemKind.Episode && (item.Season is null || item.Episode is null) ? null : new
                {
                    item.Id,
                    MediaTypeId = (byte)(item.Kind == MediaServerItemKind.Movie ? MediaType.Movie : MediaType.Series),
                    TmdbNamespace = item.Kind == MediaServerItemKind.Movie ? "tmdb_movie" : "tmdb_tv",
                    owner.TmdbId,
                    owner.ImdbId,
                    owner.TvdbId,
                    item.Season,
                    item.Episode,
                    PositionMs = Math.Min(item.PositionMs, MaximumPositionMs),
                    item.IsPlayed,
                    DurationMs = item.DurationMs is <= MaximumPositionMs ? item.DurationMs : null
                };
            }).Where(row => row is not null).ToArray();
            if (rows.Length == 0)
            {
                return (0, 0);
            }

            var candidates = await context.ExecuteBatchAsync((ctx, token) =>
                ctx.ReadProgressCandidatesAsync(new MediaServerItemsData(link.Id, link.ProfileId, JsonSerializer.Serialize(rows)), token), ct);
            var taken = new HashSet<(long, long?)>();
            var local = new List<object>();
            var states = new List<object>();
            var remote = 0;
            foreach (var candidate in candidates)
            {
                if (MediaServerLogic.Merge(candidate) is not { } decision)
                {
                    continue;
                }

                if (decision.WritesRemote)
                {
                    await _client.SaveUserDataAsync(connection, link.ExternalUserId, candidate.ExternalItemId, decision.PositionMs, decision.IsCompleted, ct);
                    remote++;
                }

                // Two server items of one Work (two versions of a film) move it once.
                if (decision.WritesLocal && taken.Add((candidate.WorkId, candidate.WorkUnitId)))
                {
                    local.Add(new { candidate.WorkId, candidate.WorkUnitId, decision.PositionMs, candidate.DurationMs, decision.IsCompleted });
                }

                states.Add(new { candidate.ExternalItemId, decision.PositionMs, decision.IsCompleted });
            }

            if (states.Count > 0)
            {
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    await ctx.SaveSyncedProgressAsync(new SyncedProgressData(link.Id, link.ProfileId, JsonSerializer.Serialize(local), JsonSerializer.Serialize(states)), token);
                    return true;
                }, ct);
            }

            return (local.Count, remote);
        }

        private string Unprotect(byte[] secret)
        {
            byte[] clear;
            try
            {
                clear = _protector.Unprotect(secret);
            }
            catch (CryptographicException)
            {
                throw new ServiceUnavailableException("media_server_authentication_failed");
            }

            try
            {
                return Encoding.UTF8.GetString(clear);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }

        private static ServiceUnavailableException Translate(MediaServerException exception) => new(exception.Failure switch
        {
            MediaServerFailure.Unauthorized => "media_server_authentication_failed",
            MediaServerFailure.InvalidResponse => "media_server_invalid_response",
            _ => "media_server_unavailable"
        });
    }
}
