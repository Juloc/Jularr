using Jularr.Backend.Data.Common;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Media;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.User.Playback.V1
{
    // Subtitles as WebVTT: SubRip and WebVTT files directly, everything else once through FFmpeg into the playback cache.
    internal sealed class PlaybackSubtitles(MediaTranscoder transcoder, IOptions<MediaOptions> options)
    {
        // Embedded subtitles read the whole media file; few conversions run at once.
        private static readonly SemaphoreSlim Conversions = new(2, 2);

        public bool CanConvert => transcoder.IsAvailable;

        public async Task<string> ReadAsync(string path, int? streamIndex, string cacheKey, CancellationToken ct)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (streamIndex is null && new FileInfo(path).Length > PlaybackTracks.MaximumSubtitleBytes)
            {
                throw new ServiceUnavailableException("playback_subtitle_unavailable");
            }

            if (streamIndex is null && extension is ".srt" or ".vtt")
            {
                var text = PlaybackTracks.Decode(await File.ReadAllBytesAsync(path, ct));
                return extension == ".vtt" ? text : PlaybackTracks.SrtToVtt(text);
            }

            if (!transcoder.IsAvailable)
            {
                throw new ServiceUnavailableException("playback_conversion_unavailable");
            }

            var folder = Path.Combine(options.Value.TranscodeCachePath, "subtitles");
            var cached = Path.Combine(folder, cacheKey + ".vtt");
            if (File.Exists(cached))
            {
                return await File.ReadAllTextAsync(cached, ct);
            }

            Directory.CreateDirectory(folder);
            var pending = Path.Combine(folder, cacheKey + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await Conversions.WaitAsync(ct);
            try
            {
                if (File.Exists(cached))
                {
                    return await File.ReadAllTextAsync(cached, ct);
                }

                if (!await transcoder.ConvertSubtitleAsync(path, streamIndex is null ? "ass" : null, streamIndex, pending, ct))
                {
                    throw new ServiceUnavailableException("playback_subtitle_unavailable");
                }

                File.Move(pending, cached, true);
                return await File.ReadAllTextAsync(cached, ct);
            }
            finally
            {
                Conversions.Release();
                File.Delete(pending);
            }
        }
    }
}
