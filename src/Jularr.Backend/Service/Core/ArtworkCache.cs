using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Logic.Storage;
using SkiaSharp;

namespace Jularr.Backend.Service.Core
{
    // Persistent, rebuildable browser WebP renditions. JPEG XL requires a separately verified native encoder.
    internal sealed class ArtworkCache
    {
        private const int MaxSourceBytes = 12 * 1024 * 1024;
        private const long MaxSourcePixels = 32_000_000;
        private const int PosterWidth = 384;
        private static readonly SemaphoreSlim Encoder = new(1, 1);
        private readonly string _path;

        public ArtworkCache(string dataPath)
        {
            if (!Path.IsPathFullyQualified(dataPath)) throw new ArgumentException("Artwork cache requires an absolute persistent DataPath.", nameof(dataPath));
            _path = Path.Combine(dataPath, "cache", "artwork");
        }

        public async Task WarmAsync(LogicContext context, StorageFiles files, string rootPath, long libraryRootId, CancellationToken ct)
        {
            var (settings, candidates) = await context.ExecuteBatchAsync(async (ctx, token) =>
                (await ctx.ReadArtworkCacheSettingsAsync(token),
                    await ctx.ReadArtworkCacheCandidatesAsync(new ArtworkCacheRootData(libraryRootId), token)), ct);
            if (settings.CacheLimitBytes == 0 || candidates.Count == 0) return;

            await Encoder.WaitAsync(ct);
            try
            {
                Directory.CreateDirectory(_path);
                var used = Directory.EnumerateFiles(_path, "*.webp", SearchOption.TopDirectoryOnly).Sum(path => new FileInfo(path).Length);
                foreach (var candidate in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    if (candidate.SourceSizeBytes is null or <= 0 or > MaxSourceBytes || used >= settings.CacheLimitBytes) break;

                    try
                    {
                        var path = StoragePath.Combine(rootPath, candidate.SourceKey);
                        await using var stream = await files.OpenReadAsync(path, ct);
                        using var buffer = new MemoryStream();
                        var chunk = new byte[65536];
                        int read;
                        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
                        {
                            if (buffer.Length + read > MaxSourceBytes) throw new InvalidDataException("Artwork exceeds the size limit.");
                            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
                        }

                        var source = buffer.ToArray();
                        var rendered = Encode(source, settings.CacheQualityId);
                        if (rendered is null || rendered.Value.WebpBytes.Length > 2 * 1024 * 1024) continue;
                        var value = rendered.Value;
                        if (used + value.WebpBytes.Length > settings.CacheLimitBytes) break;

                        var sourceHash = Convert.ToHexStringLower(SHA256.HashData(source));
                        var hash = Convert.ToHexStringLower(SHA256.HashData(value.WebpBytes));
                        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceHash + ":webp:" +
                            (byte)settings.CacheQualityId + ":" + value.Width + ":" + value.Height + ":v1")));
                        var target = Path.Combine(_path, key + ".webp");
                        if (!File.Exists(target))
                        {
                            var temporary = Path.Combine(_path, Guid.NewGuid().ToString("N") + ".partial");
                            try
                            {
                                await File.WriteAllBytesAsync(temporary, value.WebpBytes, ct);
                                File.Move(temporary, target, overwrite: false);
                                used += value.WebpBytes.Length;
                            }
                            finally
                            {
                                if (File.Exists(temporary)) File.Delete(temporary);
                            }
                        }

                        var result = new ArtworkRenditionSaveData(candidate.ArtworkId, candidate.WorkId, candidate.SlotId, value.Width, value.Height,
                            key, sourceHash, hash, value.WebpBytes.Length, settings.CacheQualityId, value.FallbackBytes);
                        await context.ExecuteBatchAsync(async (ctx, token) =>
                        {
                            await ctx.SaveArtworkRenditionAsync(result, token);
                            return true;
                        }, ct);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        // An unreadable or unsupported sidecar does not invalidate the last working rendition.
                    }
                }
            }
            finally
            {
                Encoder.Release();
            }
        }

        // A cached response never opens the NAS or downloads provider images.
        public byte[]? Read(string? key)
        {
            if (key is not { Length: 64 } || !key.All(char.IsAsciiHexDigit)) return null;
            var path = Path.Combine(_path, key + ".webp");
            try
            {
                var info = new FileInfo(path);
                return info.Exists && info.Length is > 0 and <= 2097152 ? File.ReadAllBytes(path) : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static (int Width, int Height, byte[] WebpBytes, byte[]? FallbackBytes)? Encode(byte[] source, ArtworkQuality profile)
        {
            using var header = SKCodec.Create(new MemoryStream(source, writable: false));
            if (header is null || header.Info.Width is < 16 or > 8000 || header.Info.Height is < 16 or > 8000 ||
                (long)header.Info.Width * header.Info.Height > MaxSourcePixels)
            {
                return null;
            }

            using var original = SKBitmap.Decode(source);
            if (original is null) return null;

            var width = Math.Min(PosterWidth, original.Width);
            var height = Math.Max(16, (int)Math.Round((double)original.Height * width / original.Width));
            if (height > 8000) return null;

            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            if (surface is null) return null;
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.DrawBitmap(original, new SKRect(0, 0, width, height));
            surface.Canvas.Flush();

            var quality = profile switch
            {
                ArtworkQuality.Balanced => 76,
                ArtworkQuality.High => 85,
                ArtworkQuality.VisuallyLossless => 94,
                ArtworkQuality.Lossless => 100,
                _ => 85
            };

            using var image = surface.Snapshot();
            using var webp = image.Encode(SKEncodedImageFormat.Webp, quality);
            if (webp is null) return null;

            var previewWidth = Math.Min(96, width);
            var previewHeight = Math.Max(16, (int)Math.Round((double)height * previewWidth / width));
            using var previewSurface = SKSurface.Create(new SKImageInfo(previewWidth, previewHeight));
            if (previewSurface is null) return null;
            previewSurface.Canvas.Clear(SKColors.Transparent);
            previewSurface.Canvas.DrawImage(image, new SKRect(0, 0, previewWidth, previewHeight));
            previewSurface.Canvas.Flush();
            using var previewImage = previewSurface.Snapshot();
            using var tiny = previewImage.Encode(SKEncodedImageFormat.Webp, 55);
            var fallback = tiny is { Size: > 0 and <= 65536 } ? tiny.ToArray() : null;
            return (width, height, webp.ToArray(), fallback);
        }
    }
}
