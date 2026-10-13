using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Data.MusicBrainz;
using Jularr.Integrations.Logic.MusicBrainz;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // MusicBrainz lookups and imports: an album is the Work, its tracks are the units.
    internal sealed class MusicMetadata(MusicBrainzClient musicBrainz)
    {
        public const string Namespace = "musicbrainz";

        public static bool Handles(MediaType mediaType) => mediaType == MediaType.Music;

        public async Task<IReadOnlyList<MusicBrainzAlbum>> SearchAsync(string query, int? year, CancellationToken ct)
        {
            try
            {
                var results = await musicBrainz.SearchAsync(query, ct);
                return year is { } wanted ? [.. results.OrderByDescending(result => result.Year == wanted)] : results;
            }
            catch (MusicBrainzException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<long> ImportAsync(LogicContext context, string id, CancellationToken ct)
        {
            // Read before the short write batch.
            MusicBrainzAlbumDetails details;
            try
            {
                details = await musicBrainz.ReadAsync(id, ct);
            }
            catch (MusicBrainzException exception)
            {
                throw Translate(exception);
            }

            var album = details.Album;
            var facts = new[]
            {
                new { Key = "Artist", Value = album.Artist, Localized = false },
                new { Key = "Poster", Value = MusicBrainzClient.CoverUrl(album.Id), Localized = false }
            }.Where(fact => !string.IsNullOrWhiteSpace(fact.Value));
            var tracks = details.Tracks.Select(track => new { Track = track.Number, track.Title });
            var data = new WorkMetadataData(MediaType.Music, null, Namespace, album.Id, album.Title, null, album.Year is >= 1800 and <= 2200 ? album.Year : null, "[]", JsonSerializer.Serialize(tracks),
                JsonSerializer.Serialize(facts), Namespace);
            return await context.ExecuteBatchAsync((ctx, token) => ctx.SaveWorkMetadataAsync(data, token), ct);
        }

        private static Exception Translate(MusicBrainzException exception) => exception.Failure switch
        {
            MusicBrainzFailure.NotFound => new KeyNotFoundException("MusicBrainz does not know this album."),
            MusicBrainzFailure.RateLimited => new ServiceUnavailableException("metadata_rate_limited"),
            _ => new ServiceUnavailableException("metadata_unavailable")
        };
    }
}
