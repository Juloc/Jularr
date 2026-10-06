using System.Security.Cryptography;
using System.Text;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Artwork;

/// <summary>
/// The local, Jularr-owned copies of provider artwork of durable Works (#820 on top of the #570 cache): compact WebP derivatives under
/// <c>/data/cache/artwork/work</c>, so Library and Detail pages render artwork while providers are unreachable. The file of a variant
/// is named by its cache key alone, a hash of the variant's identity, so no provider or user input ever becomes part of a path.
/// Provider URLs are untrusted: only HTTPS on an allow-listed image host is fetched, the final address after redirects is checked
/// again, and the body must be a decodable JPEG/PNG/WebP within <see cref="AnimeArtworkFiles.MaxImageBytes"/>.
/// </summary>
public sealed class WorkArtworkCache(string rootPath, IReadOnlyCollection<string> allowedHosts, IHttpClientFactory httpClients)
{
    public const string DefaultRootPath = "/data/cache/artwork/work";

    // Part of every key: bumping it re-derives all variants instead of serving derivatives of an older encoding.
    private const string DerivativeVersion = "v1:webp82:poster512:wide1600";

    public string RootPath { get; } = rootPath;

    /// <summary>The deterministic key of one variant's derivative: 32 lower-case hex characters.</summary>
    public static string CacheKey(Guid workId, WorkArtworkSlot slot, string language, string source, string providerFilePath)
    {
        var identity = string.Join('\u001f', DerivativeVersion, workId.ToString("N"), slot.ToString(), language, source, providerFilePath);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
    }

    public static bool IsCacheKey(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigitLower);

    /// <summary>Where the derivative of <paramref name="cacheKey"/> lives; null for anything that is not a cache key.</summary>
    public string? PathFor(string? cacheKey) =>
        IsCacheKey(cacheKey) ? Path.Combine(RootPath, cacheKey![..2], cacheKey + ".webp") : null;

    public bool IsHostAllowed(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Makes sure the derivative of <paramref name="cacheKey"/> exists, downloading <paramref name="source"/> only when it does not.
    /// Returns false when the image itself is unusable (refused address, error status, not an image, too large, undecodable); such an
    /// image is skipped, not retried. Network failures and timeouts propagate: they are transient and the caller retries later.
    /// </summary>
    public async Task<bool> EnsureAsync(Uri source, WorkArtworkSlot slot, string cacheKey, CancellationToken cancellationToken)
    {
        var path = PathFor(cacheKey) ?? throw new ArgumentException("Not an artwork cache key.", nameof(cacheKey));
        if (File.Exists(path))
        {
            return true;
        }

        if (!IsHostAllowed(source))
        {
            return false;
        }

        using var client = httpClients.CreateClient(AnimeArtworkLibrary.HttpClientName);
        using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode
            || response.RequestMessage?.RequestUri is not { } finalUri
            || !IsHostAllowed(finalUri)
            || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true
            || response.Content.Headers.ContentLength > AnimeArtworkFiles.MaxImageBytes)
        {
            return false;
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = await AnimeArtworkFiles.ReadLimitedAsync(body, cancellationToken);
        if (bytes is null || AnimeArtworkFiles.DetectExtension(bytes) is null)
        {
            return false;
        }

        await using var input = new MemoryStream(bytes, writable: false);
        await using var output = new MemoryStream();
        var kind = slot == WorkArtworkSlot.Poster ? AnimeArtworkKind.Poster : AnimeArtworkKind.Fanart;
        if (!await AnimeArtworkStore.CreateOptimizedDerivativeAsync(kind, input, output, cancellationToken))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AnimeArtworkFiles.WriteAtomicAsync(path, output.ToArray(), cancellationToken);
        return true;
    }

    public void Delete(string? cacheKey)
    {
        if (PathFor(cacheKey) is { } path)
        {
            AnimeArtworkFiles.TryDelete(path);
        }
    }

    /// <summary>
    /// Up to <paramref name="limit"/> cache keys whose derivative was written before <paramref name="olderThanUtc"/>, for the orphan
    /// sweep. The age guard keeps a file that was just downloaded but whose variant row is not committed yet out of the sweep.
    /// </summary>
    public IReadOnlyList<string> ListCachedKeys(DateTime olderThanUtc, int limit)
    {
        if (!Directory.Exists(RootPath))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(RootPath, "*.webp", SearchOption.AllDirectories)
                .Where(file => File.GetLastWriteTimeUtc(file) < olderThanUtc)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(IsCacheKey)
                .Select(key => key!)
                .Take(limit)
        ];
    }
}
