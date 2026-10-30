using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.AniList;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.AniList;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Integrations.Data.AniList;
using Jularr.Integrations.Logic.AniList;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Core
{
    // Each profile's own AniList account: signing in with the Owner's application, reading the list and exchanging progress. Shared by the profile's services and the System pass.
    internal sealed class AniListAccounts
    {
        private const int RoundLimit = 50;
        private readonly AniListClient _client;
        private readonly IDataProtector _protector;
        private readonly ProviderOptions _providers;
        private readonly TimeProvider _time;

        public AniListAccounts(AniListClient client, IDataProtectionProvider protection, ProviderOptions providers, TimeProvider time)
        {
            _client = client;
            _protector = protection.CreateProtector(SaveProviderSecretV1.ProtectorPurpose);
            _providers = providers;
            _time = time;
        }

        // AniList's own PIN page shows the code a person pastes into Jularr, so a server needs no public address.
        private Uri RedirectUri => new(_providers.AniListAccount, "api/v2/oauth/pin");

        public string? AuthorizationUrl(string? clientId) => clientId is null ? null
            : new Uri(_providers.AniListAccount, "api/v2/oauth/authorize").AbsoluteUri + "?client_id=" + Uri.EscapeDataString(clientId)
                + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri.AbsoluteUri) + "&response_type=code";

        // The code becomes a token of the person's own account; only a token AniList accepts for a person is stored.
        public async Task<AniListConnectionSaveData> ConnectAsync(LogicContext context, long profileId, string code, CancellationToken ct)
        {
            var application = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAniListApplicationAsync(token), ct);
            if (application is not { IsEnabled: true, ClientId: { } clientId, ProtectedSecret: { } secret })
            {
                throw new ServiceUnavailableException("anilist_not_configured");
            }

            try
            {
                var exchanged = await _client.ExchangeCodeAsync(new Uri(_providers.AniListAccount, "api/v2/oauth/token"), RedirectUri, clientId, Unprotect(secret), code, ct);
                var viewer = await _client.ReadViewerAsync(exchanged.AccessToken, ct);
                return new AniListConnectionSaveData(profileId, viewer.Id, viewer.Name.Length > 64 ? viewer.Name[..64] : viewer.Name, Protect(exchanged.AccessToken), exchanged.ExpiresAt?.UtcDateTime);
            }
            catch (AniListException exception)
            {
                throw Translate(exception);
            }
        }

        // One pass over every connected profile: its list is read, finished titles are taken over, and progress made since sending was switched on is sent.
        // A profile whose sign-in no longer works waits for its own reconnect; an unreachable AniList fails the pass after the others were served; a limit ends it quietly.
        public async Task<bool> SyncAsync(LogicContext context, CancellationToken ct)
        {
            if (AniListClient.BlockedUntil is not null)
            {
                return false;
            }

            var connections = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAniListConnectionsAsync(token), ct);
            if (connections.Count == 0)
            {
                return false;
            }

            ServiceUnavailableException? outage = null;
            foreach (var connection in connections)
            {
                string? failure = null;
                try
                {
                    if (connection.ExpiresAt is { } expiresAt && expiresAt <= _time.GetUtcNow().UtcDateTime)
                    {
                        failure = "anilist_token_expired";
                    }
                    else
                    {
                        await SyncProfileAsync(context, connection, ct);
                    }
                }
                catch (AniListException exception) when (exception.Failure == AniListFailure.RateLimited)
                {
                    return false;
                }
                catch (AniListException exception)
                {
                    failure = Translate(exception).Code;
                    if (exception.Failure != AniListFailure.Unauthorized)
                    {
                        outage ??= Translate(exception);
                    }
                }
                catch (ServiceUnavailableException exception) when (exception.Code == "anilist_authentication_failed")
                {
                    failure = exception.Code;
                }

                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    await ctx.CompleteAniListPassAsync(new AniListPassData(connection.ProfileId, failure), token);
                    return true;
                }, ct);
            }

            return outage is null ? true : throw outage;
        }

        private async Task SyncProfileAsync(LogicContext context, AniListSyncConnectionData connection, CancellationToken ct)
        {
            var accessToken = Unprotect(connection.ProtectedToken);
            var profile = new AniListProfileData(connection.ProfileId);

            // Its list
            var entries = (await _client.ReadListAsync(accessToken, connection.ViewerId, AniListMediaType.Anime, ct)).Concat(await _client.ReadListAsync(accessToken, connection.ViewerId, AniListMediaType.Manga, ct))
                .Where(entry => AniListSyncRules.Statuses.Contains(entry.Status)).ToArray();
            var rows = JsonSerializer.Serialize(entries.Select(entry => new
            {
                entry.MediaId,
                entry.ListEntryId,
                entry.IsManga,
                entry.Title,
                entry.CoverUrl,
                entry.Status,
                entry.Progress,
                entry.ProgressVolumes,
                entry.Score,
                entry.TotalCount,
                entry.TotalVolumes,
                UpdatedAt = entry.UpdatedAt.UtcDateTime
            }));
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.SaveAniListEntriesAsync(new AniListEntriesSaveData(connection.ProfileId, rows), token);
                return true;
            }, ct);

            // Taken over
            for (var round = 0; round < RoundLimit; round++)
            {
                var batch = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAniListImportEntriesAsync(profile, token), ct);
                if (batch.Count == 0)
                {
                    break;
                }

                var ids = JsonSerializer.Serialize(batch.Select(entry => entry.MediaId));
                var units = (await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAniListUnitsAsync(new AniListUnitsReadData(connection.ProfileId, ids), token), ct)).ToLookup(unit => unit.MediaId);
                var completions = JsonSerializer.Serialize(batch.SelectMany(entry => AniListSyncRules.PlanImport(entry, [.. units[entry.MediaId]])).DistinctBy(unit => (unit.WorkId, unit.WorkUnitId))
                    .Select(unit => new { unit.WorkId, unit.WorkUnitId, unit.IsVolume }));
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    await ctx.ApplyAniListImportAsync(new AniListImportData(connection.ProfileId, completions, ids), token);
                    return true;
                }, ct);
            }

            // Sent
            if (connection.PushEnabledAt is not { } pushEnabledAt)
            {
                return;
            }

            var checkedAt = _time.GetUtcNow().UtcDateTime;
            for (var round = 0; round < RoundLimit; round++)
            {
                var batch = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAniListPushEntriesAsync(profile, token), ct);
                if (batch.Count == 0)
                {
                    break;
                }

                var ids = JsonSerializer.Serialize(batch.Select(entry => entry.MediaId));
                var units = (await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAniListUnitsAsync(new AniListUnitsReadData(connection.ProfileId, ids), token), ct)).ToLookup(unit => unit.MediaId);
                var failed = new HashSet<int>();
                foreach (var entry in batch)
                {
                    if (AniListSyncRules.PlanPush(entry, [.. units[entry.MediaId]], pushEnabledAt) is not { } push)
                    {
                        continue;
                    }

                    try
                    {
                        var saved = await _client.SaveProgressAsync(accessToken, push.ListEntryId, push.IsVolume, push.Progress, push.StartsTracking, _time.GetUtcNow(), ct);
                        await context.ExecuteBatchAsync(async (ctx, token) =>
                        {
                            await ctx.RecordAniListPushedAsync(new AniListPushedData(connection.ProfileId, push.MediaId, saved.Progress, saved.ProgressVolumes, saved.UpdatedAt.UtcDateTime), token);
                            return true;
                        }, ct);
                    }
                    catch (AniListException exception) when (exception.Failure is not (AniListFailure.RateLimited or AniListFailure.Unauthorized))
                    {
                        // Tried again after a growing wait, beside the entries that went through.
                        failed.Add(push.MediaId);
                        var next = _time.GetUtcNow().UtcDateTime + AniListSyncRules.RetryDelay(entry.AttemptCount + 1);
                        await context.ExecuteBatchAsync(async (ctx, token) =>
                        {
                            await ctx.RecordAniListFailedAsync(new AniListFailedData(connection.ProfileId, push.MediaId, Translate(exception).Code, next), token);
                            return true;
                        }, ct);
                    }
                }

                var looked = JsonSerializer.Serialize(batch.Select(entry => entry.MediaId).Where(id => !failed.Contains(id)));
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    await ctx.MarkAniListCheckedAsync(new AniListCheckedData(connection.ProfileId, looked, checkedAt), token);
                    return true;
                }, ct);
            }
        }

        public byte[] Protect(string token) => _protector.Protect(Encoding.UTF8.GetBytes(token));

        private string Unprotect(byte[] secret)
        {
            byte[] clear;
            try
            {
                clear = _protector.Unprotect(secret);
            }
            catch (CryptographicException)
            {
                throw new ServiceUnavailableException("anilist_authentication_failed");
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

        private static ServiceUnavailableException Translate(AniListException exception) => new(exception.Failure switch
        {
            AniListFailure.Unauthorized => "anilist_authentication_failed",
            AniListFailure.RateLimited => "anilist_rate_limited",
            AniListFailure.InvalidResponse => "anilist_invalid_response",
            _ => "anilist_unavailable"
        });
    }
}
