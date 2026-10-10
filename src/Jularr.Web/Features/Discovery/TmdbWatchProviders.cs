using System.Globalization;
using System.Text.Json;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Discovery;

public sealed record TmdbWatchOffer(
    int ProviderId,
    string ProviderName,
    string Category,
    int DisplayPriority);

public sealed record TmdbWatchAvailability(
    string Region,
    Uri WatchPage,
    IReadOnlyList<TmdbWatchOffer> Offers)
{
    public string Attribution => "JustWatch";
}

public sealed partial class TmdbDiscoveryProvider
{
    private static readonly (string Field, string Category)[] WatchCategories =
    [
        ("flatrate", "streaming"),
        ("free", "free"),
        ("ads", "ads"),
        ("rent", "rent"),
        ("buy", "buy")
    ];

    public async Task<TmdbWatchAvailability?> GetWatchAvailabilityAsync(
        long workId,
        WorkMediaType mediaType,
        string region,
        CancellationToken cancellationToken)
    {
        if (workId <= 0 || mediaType is not (WorkMediaType.Movie or WorkMediaType.Series))
        {
            return null;
        }

        if (region is null || region.Length != 2 ||
            !region.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException(
                "An explicit ISO 3166-1 alpha-2 region is required.", nameof(region));
        }

        // Use only confirmed canonical Work identities; never match by title.
        var ids = await db.WorkExternalIdentities.AsNoTracking()
            .Where(x => x.WorkId == workId &&
                x.MediaType == mediaType &&
                x.Provider == ProviderKeys.Tmdb &&
                x.ReviewState == MappingReviewState.Confirmed)
            .OrderBy(x => x.ExternalId)
            .Select(x => x.ExternalId)
            .Distinct()
            .Take(2)
            .ToArrayAsync(cancellationToken);
        if (ids.Length != 1 || !TryNormalizeExternalId(ids[0], out var normalizedId))
        {
            return null;
        }

        var type = mediaType == WorkMediaType.Movie ? "movie" : "tv";
        var key = $"tmdb:watch:{type}:{normalizedId}:{region}";
        var response = await cache.GetOrFetchAsync(
            key,
            TimeSpan.FromHours(3),
            ct => GetJsonAsync<JsonElement>(
                $"{type}/{normalizedId}/watch/providers", [], ct),
            cancellationToken);
        return ReadWatchAvailability(response, type, normalizedId, region);
    }

    internal static TmdbWatchAvailability? ReadWatchAvailability(
        JsonElement response,
        string type,
        string externalId,
        string region)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Object ||
            !results.TryGetProperty(region, out var selected) ||
            selected.ValueKind != JsonValueKind.Object ||
            !selected.TryGetProperty("link", out var link) ||
            link.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var watchPage) ||
            watchPage.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(watchPage.Host, "www.themoviedb.org", StringComparison.OrdinalIgnoreCase) ||
            !watchPage.AbsolutePath.StartsWith(
                $"/{type}/{externalId}/watch", StringComparison.Ordinal) &&
            !watchPage.AbsolutePath.StartsWith(
                $"/{type}/{externalId}-", StringComparison.Ordinal))
        {
            return null;
        }

        var offers = new List<TmdbWatchOffer>();
        foreach (var (field, category) in WatchCategories)
        {
            if (!selected.TryGetProperty(field, out var providers) ||
                providers.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var provider in providers.EnumerateArray().Take(80))
            {
                if (provider.ValueKind != JsonValueKind.Object ||
                    !provider.TryGetProperty("provider_id", out var id) ||
                    id.ValueKind != JsonValueKind.Number ||
                    !id.TryGetInt32(out var numericId) || numericId <= 0 ||
                    !provider.TryGetProperty("provider_name", out var name) ||
                    name.ValueKind != JsonValueKind.String ||
                    name.GetString() is not { Length: > 0 and <= 100 } providerName)
                {
                    continue;
                }

                var priority = provider.TryGetProperty("display_priority", out var order) &&
                    order.ValueKind == JsonValueKind.Number && order.TryGetInt32(out var position)
                    ? position : int.MaxValue;
                offers.Add(new TmdbWatchOffer(
                    numericId, providerName, category, priority));
            }
        }

        var ordered = offers
            .DistinctBy(x => (x.ProviderId, x.Category))
            .OrderBy(x => x.DisplayPriority)
            .ThenBy(x => x.ProviderId)
            .ThenBy(x => x.Category, StringComparer.Ordinal)
            .Take(30)
            .ToArray();

        return ordered.Length == 0
            ? null
            : new TmdbWatchAvailability(region, watchPage, ordered);
    }
}
