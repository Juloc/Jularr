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
