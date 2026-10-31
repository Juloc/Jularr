using System.Text.Json;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Newznab;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Data.Qbittorrent;
using Jularr.Integrations.Data.Usenet;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Sabnzbd;
using Jularr.Integrations.Logic.Qbittorrent;
using Jularr.Integrations.Data.Subtitles;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    // The one test of an acquisition connection: the Test action of the connections page and the health check ask the same.
    internal sealed class AcquisitionConnectionProbe
    {
        private readonly NewznabClient _newznab;
        private readonly SabnzbdClient _sabnzbd;
        private readonly QbittorrentClient _qbittorrent;
        private readonly DirectSources _direct;
        private readonly NyaaSources _nyaa;
        private readonly UsenetJob _usenet;
        private readonly SubtitleProviders _subtitles;
        private readonly IDataProtector _protector;

        public AcquisitionConnectionProbe(NewznabClient newznab, SabnzbdClient sabnzbd, QbittorrentClient qbittorrent, DirectSources direct, NyaaSources nyaa, UsenetJob usenet, SubtitleProviders subtitles, IDataProtectionProvider protection)
        {
            _subtitles = subtitles;
            _usenet = usenet;
            _newznab = newznab;
            _sabnzbd = sabnzbd;
            _qbittorrent = qbittorrent;
            _direct = direct;
            _nyaa = nyaa;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        // The result, and the capabilities an indexer reported to be kept with the connection.
        public async Task<(AcquisitionConnectionTestV1 Result, string? Capabilities)> TestAsync(AcquisitionConnectionData connection, CancellationToken ct)
        {
            var apiKey = DirectSources.IsDirect(connection.AcquisitionConnectionTypeId)
                || connection.AcquisitionConnectionTypeId is AcquisitionConnectionType.Usenet or AcquisitionConnectionType.OpenSubtitles or AcquisitionConnectionType.SubDl
                ? ""
                : connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Newznab
                    ? AcquisitionConnectionInput.IndexerKey(_protector, connection.ProtectedApiKey)
                    : AcquisitionConnectionInput.Unprotect(_protector, connection.ProtectedApiKey);

            if (DirectSources.IsDirect(connection.AcquisitionConnectionTypeId))
            {
                var error = await _direct.TestAsync(connection, ct);
                string[] serves = connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Narou ? ["LightNovel"] : ["Book"];
                return (new AcquisitionConnectionTestV1(error is null, error, null, error is null ? serves : []), null);
            }

            if (connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Nyaa)
            {
                // One search proves the index of the torrents.
                var error = await _nyaa.TestAsync(connection, ct);
                return (new AcquisitionConnectionTestV1(error is null, error, null, error is null ? ["Book"] : []), null);
            }

            if (connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet)
            {
                // A login and one command prove the news server.
                try
                {
                    await _usenet.TestAsync(connection, ct);
                    return (new AcquisitionConnectionTestV1(true, null, null, []), null);
                }
                catch (UsenetException exception)
                {
                    return (new AcquisitionConnectionTestV1(false, exception.Failure switch
                    {
                        UsenetFailure.Unauthorized => "usenet_credentials_rejected",
                        UsenetFailure.ConnectionLimit => "usenet_connection_limit",
                        _ => "acquisition_unavailable"
                    }, null, []), null);
                }
            }

            if (connection.AcquisitionConnectionTypeId is AcquisitionConnectionType.OpenSubtitles or AcquisitionConnectionType.SubDl)
            {
                // A login or one search proves the subtitle provider.
                try
                {
                    await _subtitles.TestAsync(connection, ct);
                    return (new AcquisitionConnectionTestV1(true, null, null, []), null);
                }
                catch (SubtitleException exception)
                {
                    return (new AcquisitionConnectionTestV1(false, exception.Code, null, []), null);
                }
            }

            if (connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Newznab)
            {
                try
                {
                    var caps = await _newznab.ReadCapabilitiesAsync(new NewznabConnection(connection.HttpAddress, apiKey), ct);
                    return (new AcquisitionConnectionTestV1(true, null, caps.Version, caps.Modes.Keys.Order().Select(mode => mode.ToString()).ToArray()), JsonSerializer.Serialize(caps));
                }
                catch (NewznabException exception)
                {
                    return (new AcquisitionConnectionTestV1(false, AcquisitionConnectionInput.ErrorKey(exception.Failure), null, []), null);
                }
            }

            if (connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Qbittorrent)
            {
                // A login and the version prove the torrent client.
                var torrentTest = await _qbittorrent.TestAsync(new QbittorrentConnection(connection.HttpAddress, connection.Username,
                    AcquisitionConnectionInput.Unprotect(_protector, connection.ProtectedPassword)), ct);
                var torrentError = torrentTest.IsAvailable ? null : torrentTest.Version is null ? "acquisition_unavailable" : "acquisition_authentication_failed";
                return (new AcquisitionConnectionTestV1(torrentTest.IsAvailable, torrentError, torrentTest.Version, []), null);
            }
            var test = await _sabnzbd.TestAsync(new SabnzbdConnection(connection.HttpAddress, apiKey), ct);
            var errorKey = test.IsAvailable ? null : test.Version is null ? "acquisition_unavailable" : "acquisition_authentication_failed";
            return (new AcquisitionConnectionTestV1(test.IsAvailable, errorKey, test.Version, []), null);
        }
    }
}
