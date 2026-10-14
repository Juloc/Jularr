using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Provider;
using Jularr.Integrations.Data.Plex;
using Jularr.Integrations.Logic.Plex;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Provider.V1
{
    // The owner's Plex Media Server; shared by Admin, the import and the System sync.
    internal sealed class PlexServer
    {
        private readonly PlexServerClient _plex;
        private readonly IDataProtector _protector;

        public PlexServer(PlexServerClient plex, IDataProtectionProvider protection)
        {
            _plex = plex;
            _protector = protection.CreateProtector(SaveProviderSecretV1.ProtectorPurpose);
        }

        public byte[] Protect(string token) => _protector.Protect(Encoding.UTF8.GetBytes(token));

        // Null when no server is connected or Plex is switched off.
        public async Task<PlexServerConnection?> ConnectAsync(LogicContext context, CancellationToken ct)
        {
            var state = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadPlexServerAsync(token), ct);
            if (state is not { IsEnabled: true, BaseUrl: { } baseUrl, ProtectedSecret: { } secret })
            {
                return null;
            }

            return new PlexServerConnection(new Uri(baseUrl), Unprotect(secret));
        }

        public async Task<PlexServerInfo> ReadServerAsync(PlexServerConnection connection, CancellationToken ct)
        {
            try
            {
                return await _plex.ReadServerAsync(connection, ct);
            }
            catch (PlexServerException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<PlexItemsResultData> SyncAsync(LogicContext context, PlexServerConnection connection, CancellationToken ct)
        {
            // Page by page in short batches; removals only after every library was read.
            var server = await ReadServerAsync(connection, ct);
            var seen = new List<string>();
            var linked = 0;
            var created = 0;
            try
            {
                foreach (var section in server.Sections)
                {
                    var mediaType = section.Kind == PlexSectionKind.Movie ? MediaType.Movie : MediaType.Series;
                    for (var start = 0; ; start += PlexServerClient.PageSize)
                    {
                        var page = await _plex.ReadItemsAsync(connection, section, start, ct);
                        seen.AddRange(page.Items.Select(item => item.RatingKey));
                        if (page.Items.Count > 0)
                        {
                            var rows = JsonSerializer.Serialize(page.Items.Select(item => new
                            {
                                Key = item.RatingKey,
                                MediaTypeId = (byte)mediaType,
                                item.Title,
                                item.Year,
                                item.TmdbId,
                                item.ImdbId,
                                item.TvdbId
                            }));
                            var result = await context.ExecuteBatchAsync((ctx, token) => ctx.SavePlexItemsAsync(new PlexItemsData(rows), token), ct);
                            linked += result.LinkedCount;
                            created += result.CreatedCount;
                        }

                        if (page.Items.Count < PlexServerClient.PageSize || start + page.Items.Count >= page.TotalSize)
                        {
                            break;
                        }
                    }
                }
            }
            catch (PlexServerException exception)
            {
                throw Translate(exception);
            }

            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.CompletePlexSyncAsync(new PlexKeysData([.. seen]), token);
                return true;
            }, ct);
            return new PlexItemsResultData(linked, created);
        }

        public async Task RefreshAsync(LogicContext context, MediaType mediaType, CancellationToken ct)
        {
            // Plex scans its libraries of that kind; an unreachable Plex never fails an import.
            if (mediaType is not (MediaType.Movie or MediaType.Series) || await ConnectAsync(context, ct) is not { } connection)
            {
                return;
            }

            try
            {
                var kind = mediaType == MediaType.Movie ? PlexSectionKind.Movie : PlexSectionKind.Show;
                foreach (var section in (await _plex.ReadServerAsync(connection, ct)).Sections.Where(section => section.Kind == kind))
                {
                    await _plex.RefreshSectionAsync(connection, section, ct);
                }
            }
            catch (PlexServerException)
            {
            }
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
                throw new ServiceUnavailableException("plex_authentication_failed");
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

        private static ServiceUnavailableException Translate(PlexServerException exception) => new(exception.Failure switch
        {
            PlexServerFailure.Unauthorized => "plex_authentication_failed",
            PlexServerFailure.InvalidResponse => "plex_invalid_response",
            _ => "plex_unavailable"
        });
    }
}
