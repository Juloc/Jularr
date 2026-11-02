using System.Globalization;
using System.Security.Cryptography;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Service.Core;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.User.Playback.V1
{
    // A cast device cannot sign in. It gets an address that names one playback session of one profile, signed by the server and valid for a limited time.
    internal sealed class PlaybackCastTokens
    {
        private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
        private readonly ITimeLimitedDataProtector _protector;

        public PlaybackCastTokens(IDataProtectionProvider protection)
        {
            _protector = protection.CreateProtector("Jularr.Playback.Cast.V1").ToTimeLimitedDataProtector();
        }

        public string Issue(Guid sessionId, long profileId) => _protector.Protect(sessionId.ToString("D") + "|" + profileId.ToString(CultureInfo.InvariantCulture), Lifetime);

        // An address that is forged, expired or from another installation names nothing.
        public (Guid SessionId, long ProfileId) Read(string token)
        {
            try
            {
                var parts = _protector.Unprotect(token).Split('|');
                if (parts.Length == 2 && Guid.TryParseExact(parts[0], "D", out var sessionId) && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var profileId))
                {
                    return (sessionId, profileId);
                }
            }
            catch (Exception exception) when (exception is CryptographicException or FormatException)
            {
            }

            throw new KeyNotFoundException("The cast address is unavailable.");
        }
    }

    [ServiceEndpoint("GET", "/api/v1/playback/sessions/{playbackSessionId:guid}/cast")]
    public sealed class ReadPlaybackCastV1 : UserService<PlaybackSessionParametersV1>
    {
        private readonly PlaybackTranscodes _transcodes;
        private readonly PlaybackCastTokens _tokens;

        internal ReadPlaybackCastV1(PlaybackTranscodes transcodes, PlaybackCastTokens tokens)
        {
            _transcodes = transcodes;
            _tokens = tokens;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlaybackCastV1>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackSessionParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackSessionParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override Task<IServiceOutput> OnRead()
        {
            // Only a conversion of the asking profile has an HLS address to hand over.
            _ = _transcodes.Find(Parameters.PlaybackSessionId, Auth.ProfileId!.Value) ?? throw new KeyNotFoundException("The playback session is not active.");
            var token = _tokens.Issue(Parameters.PlaybackSessionId, Auth.ProfileId!.Value);
            return Task.FromResult<IServiceOutput>(new PlaybackCastV1("/api/v1/cast/" + token + "/playlist.m3u8"));
        }
    }

    // The two addresses of a cast device: the playlist and its segments (relative to the playlist, under the same token).
    [ServiceEndpoint("GET", "/api/v1/cast/{castToken}/playlist.m3u8", RateLimit = ServiceApi.CastRateLimit)]
    public sealed class ReadCastPlaylistV1 : PublicService<CastPlaylistParametersV1>
    {
        private readonly PlaybackTranscodes _transcodes;
        private readonly PlaybackCastTokens _tokens;

        internal ReadCastPlaylistV1(PlaybackTranscodes transcodes, PlaybackCastTokens tokens)
        {
            _transcodes = transcodes;
            _tokens = tokens;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CastPlaylistV1>()];
        protected override ModuleRequirement GetInstanceModules(CastPlaylistParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");

        protected override Task<IServiceOutput> OnRead()
        {
            var (sessionId, profileId) = _tokens.Read(Parameters.CastToken);
            var source = _transcodes.Find(sessionId, profileId) ?? throw new KeyNotFoundException("The playback session is not active.");
            return Task.FromResult<IServiceOutput>(new CastPlaylistV1(PlaybackTranscodes.BuildPlaylist(source)));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/cast/{castToken}/segments/{index:int}.ts", RateLimit = ServiceApi.CastRateLimit)]
    public sealed class ReadCastSegmentV1 : PublicService<CastSegmentParametersV1>
    {
        private readonly PlaybackTranscodes _transcodes;
        private readonly PlaybackCastTokens _tokens;

        internal ReadCastSegmentV1(PlaybackTranscodes transcodes, PlaybackCastTokens tokens)
        {
            _transcodes = transcodes;
            _tokens = tokens;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CastSegmentV1>()];
        protected override ModuleRequirement GetInstanceModules(CastSegmentParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            var (sessionId, profileId) = _tokens.Read(Parameters.CastToken);
            var path = await _transcodes.GetSegmentAsync(sessionId, profileId, Parameters.Index, CancellationToken);
            return new CastSegmentV1(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous));
        }
    }
}
