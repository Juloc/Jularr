using System.Globalization;
using SkiaSharp;

namespace Jularr.Web.Features.Artwork;

/// <summary>One served artwork image: the series poster or fanart, or a season poster.</summary>
public readonly record struct AnimeArtworkSlot(AnimeArtworkKind Kind, int? SeasonNumber = null)
{
    public static AnimeArtworkSlot Poster => new(AnimeArtworkKind.Poster);
    public static AnimeArtworkSlot Fanart => new(AnimeArtworkKind.Fanart);

    public static AnimeArtworkSlot SeasonPoster(int seasonNumber) =>
        new(AnimeArtworkKind.Poster, seasonNumber);

    public string Slug =>
        SeasonNumber is int season
            ? string.Create(CultureInfo.InvariantCulture, $"season-{season:00}-{AnimeArtworkFiles.BaseName(Kind)}")
            : AnimeArtworkFiles.BaseName(Kind);

    public static bool TryParse(string? slug, out AnimeArtworkSlot slot)
    {
        slot = default;
        switch (slug?.ToLowerInvariant())
        {
            case "poster":
                slot = Poster;
                return true;
            case "fanart":
                slot = Fanart;
                return true;
        }

        const string prefix = "season-";
        const string suffix = "-poster";
        if (slug is null ||
            !slug.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !slug.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(
                slug.AsSpan(prefix.Length, slug.Length - prefix.Length - suffix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var season) ||
            season > 999)
        {
            return false;
        }

        slot = SeasonPoster(season);
        return true;
    }
}

/// <summary>
/// Rebuildable WebP derivatives of the canonical artwork beside the media. The cache is never
/// canonical: deleting it loses nothing, the next library scan regenerates it from the NAS.
/// Artwork a previous version stored under /data/artwork/anime is still served from there until
/// the library scan has moved it beside the media.
/// </summary>
public sealed class AnimeArtworkCache(string rootPath, string legacyRootPath)
{
    public const string DefaultRootPath = "/data/cache/artwork/anime";
    public const string DefaultLegacyRootPath = "/data/artwork/anime";

    private const string DerivativeVersion = "v1:webp82:poster512:fanart1600";
    private static readonly string[] LegacyExtensions = [".webp", ".jpg", ".jpeg", ".png"];

    public static AnimeArtworkCache Default { get; } = new(DefaultRootPath, DefaultLegacyRootPath);

    public string RootPath { get; } = rootPath;
    public string LegacyRootPath { get; } = legacyRootPath;

    public string? GetPublicUrl(Guid animeId, AnimeArtworkSlot slot)
    {
        var path = FindPath(animeId, slot);
        if (path is null)
        {
            return null;
        }

        var version = File.GetLastWriteTimeUtc(path).Ticks;
        return $"/artwork/anime/{animeId:D}/{slot.Slug}?v={version}";
    }

    public string? FindPath(Guid animeId, AnimeArtworkSlot slot)
    {
        var derivative = DerivativePath(animeId, slot);
        if (File.Exists(derivative))
        {
            return derivative;
        }

        return slot.SeasonNumber is null ? FindLegacyPath(animeId, slot.Kind) : null;
    }

    public string LegacyDirectory(Guid animeId) =>
        Path.Combine(LegacyRootPath, animeId.ToString("N"));

    public string? FindLegacyPath(Guid animeId, AnimeArtworkKind kind)
    {
        var directory = LegacyDirectory(animeId);
        return LegacyExtensions
            .Select(extension => Path.Combine(directory, AnimeArtworkFiles.BaseName(kind) + extension))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Regenerates the slot's derivative when the canonical file (path, size or last write)
    /// changed since it was built. Returns true when a new derivative was written.
    /// </summary>
    public async Task<bool> RefreshAsync(
        Guid animeId,
        AnimeArtworkSlot slot,
        string canonicalPath,
        CancellationToken cancellationToken)
    {
        var source = new FileInfo(canonicalPath);
        if (!source.Exists || source.Length == 0 || source.Length > AnimeArtworkFiles.MaxImageBytes)
        {
            return false;
        }

        var identity = string.Join(
            '|',
            DerivativeVersion,
            source.FullName,
            source.Length.ToString(CultureInfo.InvariantCulture),
            source.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        var derivativePath = DerivativePath(animeId, slot);
        var markerPath = derivativePath + ".source";
        if (File.Exists(derivativePath) &&
            string.Equals(await TryReadAsync(markerPath, cancellationToken), identity, StringComparison.Ordinal))
        {
            return false;
        }

        var bytes = await File.ReadAllBytesAsync(source.FullName, cancellationToken);
        await using var input = new MemoryStream(bytes, writable: false);
        await using var output = new MemoryStream();
        if (!await AnimeArtworkStore.CreateOptimizedDerivativeAsync(slot.Kind, input, output, cancellationToken))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(derivativePath)!);
        await AnimeArtworkFiles.WriteAtomicAsync(derivativePath, output.ToArray(), cancellationToken);
        await AnimeArtworkFiles.WriteAtomicAsync(
            markerPath,
            System.Text.Encoding.UTF8.GetBytes(identity),
            cancellationToken);
        return true;
    }

    /// <summary>Drops a derivative whose canonical file is gone.</summary>
    public void Remove(Guid animeId, AnimeArtworkSlot slot)
    {
        var derivativePath = DerivativePath(animeId, slot);
        AnimeArtworkFiles.TryDelete(derivativePath);
        AnimeArtworkFiles.TryDelete(derivativePath + ".source");
    }

    private string DerivativePath(Guid animeId, AnimeArtworkSlot slot) =>
        Path.Combine(RootPath, animeId.ToString("N"), slot.Slug + ".webp");

    private static async Task<string?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(path)
                ? (await File.ReadAllTextAsync(path, cancellationToken)).Trim()
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Artwork URLs for pages and the client API; a remote provider URL is only the fallback.</summary>
public static class AnimeArtworkStore
{
    public const int PosterMaxWidth = 512;
    public const int FanartMaxWidth = 1600;

    private const int WebpQuality = 82;

    public static string? ResolvePosterUrl(Guid animeId, string? fallbackUrl) =>
        AnimeArtworkCache.Default.GetPublicUrl(animeId, AnimeArtworkSlot.Poster) ?? fallbackUrl;

    public static string? ResolveFanartUrl(Guid animeId, string? fallbackUrl) =>
        AnimeArtworkCache.Default.GetPublicUrl(animeId, AnimeArtworkSlot.Fanart) ?? fallbackUrl;

    /// <summary>The season's own poster, falling back to the series poster.</summary>
    public static string? ResolveSeasonPosterUrl(Guid animeId, int seasonNumber, string? fallbackUrl) =>
        AnimeArtworkCache.Default.GetPublicUrl(animeId, AnimeArtworkSlot.SeasonPoster(seasonNumber)) ??
        ResolvePosterUrl(animeId, fallbackUrl);

    /// <summary>The largest side an artwork source may have; anything bigger is refused before it is decoded.</summary>
    public const int MaxSourceDimension = 8000;

    /// <summary>The largest pixel count an artwork source may have, so a small compressed file cannot expand into gigabytes.</summary>
    public const long MaxSourcePixels = 40_000_000;

    public static async Task<bool> CreateOptimizedDerivativeAsync(
        AnimeArtworkKind kind,
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var bytes = await AnimeArtworkFiles.ReadLimitedAsync(source, cancellationToken);
        if (bytes is null || bytes.Length == 0)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (CreateOptimizedDerivative(kind, bytes) is not { } derivative)
        {
            return false;
        }

        await destination.WriteAsync(derivative, cancellationToken);
        return true;
    }

    /// <summary>
    /// The WebP derivative of a JPEG, PNG or WebP image, scaled down to the kind's width; null for anything else, for an image whose
    /// declared dimensions exceed <see cref="MaxSourceDimension"/> or <see cref="MaxSourcePixels"/> (checked from the header, before
    /// any pixel is decoded), and for an image that does not decode.
    /// </summary>
    public static byte[]? CreateOptimizedDerivative(AnimeArtworkKind kind, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        using var encoded = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(encoded);
        if (codec is null
            || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp)
            || codec.Info.Width is <= 0 or > MaxSourceDimension
            || codec.Info.Height is <= 0 or > MaxSourceDimension
            || (long)codec.Info.Width * codec.Info.Height > MaxSourcePixels)
        {
            return null;
        }

        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return null;
        }

        var maxWidth = kind == AnimeArtworkKind.Poster
            ? PosterMaxWidth
            : FanartMaxWidth;

        SKBitmap outputBitmap = bitmap;
        SKBitmap? resized = null;

        if (bitmap.Width > maxWidth)
        {
            var height = Math.Max(
                1,
                (int)Math.Round(bitmap.Height * (maxWidth / (double)bitmap.Width)));

            resized = bitmap.Resize(
                new SKSizeI(maxWidth, height),
                new SKSamplingOptions(SKCubicResampler.Mitchell));

            if (resized is null)
            {
                return null;
            }

            outputBitmap = resized;
        }

        try
        {
            using var data = outputBitmap.Encode(
                SKEncodedImageFormat.Webp,
                WebpQuality);

            if (data is null || data.Size == 0)
            {
                return null;
            }

            return data.ToArray();
        }
        finally
        {
            resized?.Dispose();
        }
    }
}
