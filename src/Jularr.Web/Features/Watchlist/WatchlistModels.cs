using System.Security.Cryptography;
using System.Text;

namespace Jularr.Web.Features.Watchlist;

public enum WatchlistMediaType
{
    Anime,
    Tv,
    Movie,
    Manga,
    LightNovel,
    Book
}

public enum WatchPreferenceState
{
    Follow,
    Ignore
}

public static class WatchlistMediaTypeNames
{
    public static string ToStorage(WatchlistMediaType type) => type switch
    {
        WatchlistMediaType.LightNovel => "lightNovel",
        _ => type.ToString().ToLowerInvariant()
    };

    public static WatchlistMediaType? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "anime" => WatchlistMediaType.Anime,
        "tv" or "series" => WatchlistMediaType.Tv,
        "movie" or "movies" or "film" => WatchlistMediaType.Movie,
        "manga" => WatchlistMediaType.Manga,
        "lightnovel" or "light-novel" or "light_novel" or "novel" => WatchlistMediaType.LightNovel,
        "book" or "books" => WatchlistMediaType.Book,
        _ => null
    };

    public static string ToCategory(WatchlistMediaType type) => type switch
    {
        WatchlistMediaType.LightNovel => "light-novel",
        WatchlistMediaType.Movie => "movie",
        WatchlistMediaType.Tv => "tv",
        _ => type.ToString().ToLowerInvariant()
    };
}

public sealed record WatchlistIdentity(
    WatchlistMediaType MediaType,
    string Provider,
    string ExternalId)
{
    public string ProviderKey => Provider.Trim().ToLowerInvariant();

    public string ExternalKey => ExternalId.Trim();

    public string Key => $"{WatchlistMediaTypeNames.ToStorage(MediaType)}:{ProviderKey}:{ExternalKey}";

    public string WatchlistUrl =>
        $"/Watchlist?mediaType={Uri.EscapeDataString(WatchlistMediaTypeNames.ToStorage(MediaType))}" +
        $"&provider={Uri.EscapeDataString(ProviderKey)}" +
        $"&externalId={Uri.EscapeDataString(ExternalKey)}#target-{StableId:D}";

    public Guid StableId
    {
        get
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Key));
            Span<byte> bytes = stackalloc byte[16];
            hash.AsSpan(0, 16).CopyTo(bytes);
            return new Guid(bytes);
        }
    }

    /// <summary>The provider's public page of the work, built from the identity only.</summary>
    public string? ProviderUrl => ProviderKey switch
    {
        "anilist" when MediaType == WatchlistMediaType.Anime => $"https://anilist.co/anime/{Uri.EscapeDataString(ExternalKey)}",
        "anilist" when MediaType is WatchlistMediaType.Manga or WatchlistMediaType.LightNovel =>
            $"https://anilist.co/manga/{Uri.EscapeDataString(ExternalKey)}",
        _ => null
    };
}

/// <summary>
/// Display data of a followed work. Library membership and links are never part of it: they
/// are resolved when the work is shown (<see cref="WatchlistLibraryResolver"/>).
/// </summary>
public sealed record WatchlistReleaseSeed(
    WatchlistMediaType MediaType,
    string ExternalId,
    string? Status);

public sealed record WatchlistDraft(
    WatchlistIdentity Identity,
    string Title,
    string? NativeTitle = null,
    string? CoverImageUrl = null,
    string? Format = null,
    string? Status = null,
    int? Year = null);

/// <param name="LocalMediaId">The library entry of the work, resolved at read time.</param>
/// <param name="DetailsUrl">The library page when the work is in the library, else the provider page.</param>
public sealed record WatchlistItem(
    WatchlistIdentity Identity,
    string Title,
    string? NativeTitle,
    string? CoverImageUrl,
    string? Format,
    string? Status,
    int? Year,
    Guid? LocalMediaId,
    string? DetailsUrl,
    Guid? FranchiseId,
    string? FranchiseTitle,
    bool IsExplicit,
    DateTime? AddedAtUtc = null)
{
    public Guid StableId => LocalMediaId ?? Identity.StableId;

    public bool IsFromFranchise => FranchiseId is not null;
}

/// <summary>Validates follow requests from the browser.</summary>
public static class WatchlistDraftInput
{
    public static bool TryCreate(
        string? mediaType,
        string? provider,
        string? externalId,
        string? title,
        string? nativeTitle,
        string? coverImageUrl,
        string? format,
        string? status,
        int? year,
        out WatchlistDraft draft)
    {
        draft = null!;
        var normalizedTitle = title?.Trim();
        if (!TryIdentity(mediaType, provider, externalId, out var identity) ||
            string.IsNullOrWhiteSpace(normalizedTitle) ||
            normalizedTitle.Length > 500 ||
            year is < 1800 or > 3000)
        {
            return false;
        }

        draft = new WatchlistDraft(
            identity,
            normalizedTitle,
            Limit(nativeTitle, 500),
            SafeImageUrl(coverImageUrl),
            Limit(format, 80),
            Limit(status, 80),
            year);
        return true;
    }

    public static bool TryIdentity(
        string? mediaType,
        string? provider,
        string? externalId,
        out WatchlistIdentity identity)
    {
        identity = null!;
        var type = WatchlistMediaTypeNames.Parse(mediaType);
        var normalizedProvider = provider?.Trim().ToLowerInvariant();
        var normalizedId = externalId?.Trim();
        if (type is null ||
            string.IsNullOrWhiteSpace(normalizedProvider) ||
            normalizedProvider.Length > 80 ||
            string.IsNullOrWhiteSpace(normalizedId) ||
            normalizedId.Length > 200)
        {
            return false;
        }

        identity = new WatchlistIdentity(type.Value, normalizedProvider, normalizedId);
        return true;
    }

    private static string? Limit(string? value, int max)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized is null ? null : normalized[..Math.Min(max, normalized.Length)];
    }

    private static string? SafeImageUrl(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized is not null &&
               normalized.Length <= 2048 &&
               Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
               uri.Scheme is "https" or "http"
            ? normalized
            : null;
    }
}
