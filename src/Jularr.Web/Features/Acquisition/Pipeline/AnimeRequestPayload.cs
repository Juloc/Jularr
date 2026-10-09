using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Wanted;

namespace Jularr.Web.Features.Acquisition.Pipeline;

/// <summary>
/// What an Anime request's search state holds beyond the shared <see cref="ReleaseRequestPayload"/>: the anime and the episodes the download in flight (or the next
/// search) is for, and the release in flight, so a failed download can be blocklisted. The importer reads the episodes from here, so a release that covers several
/// episodes (a pack) is imported against exactly what was grabbed.
/// </summary>
public sealed record AnimeRequestPayload(string? AnimeKey = null, IReadOnlyList<AnimeEpisodeKey>? Episodes = null, string? ReleaseIdentity = null, string? ReleaseTitle = null) : ReleaseRequestPayload
{
    public static AnimeRequestPayload Of(AcquisitionRequest request) => Parse(request.PayloadJson) ?? new AnimeRequestPayload();

    /// <summary>The stored payload with the anime key of a renamed series folder, so the download in flight still imports into the same anime.</summary>
    public static string? Rekey(string? stored, string oldKey, string newKey) =>
        Parse(stored) is { } payload && string.Equals(payload.AnimeKey, oldKey, StringComparison.OrdinalIgnoreCase)
            ? (payload with
            {
                AnimeKey = newKey,
                Episodes = payload.Episodes?.Select(episode => string.Equals(episode.AnimeKey, oldKey, StringComparison.OrdinalIgnoreCase) ? episode with { AnimeKey = newKey } : episode).ToArray()
            }).Serialize()
            : stored;

    public static AnimeRequestPayload? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AnimeRequestPayload>(json, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
