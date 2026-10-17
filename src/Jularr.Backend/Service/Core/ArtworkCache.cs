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
        private readonly JxlArtworkCodec _jxl;

        public ArtworkCache(string dataPath, JxlArtworkCodec? jxl = null)
        {
            if (!Path.IsPathFullyQualified(dataPath)) throw new ArgumentException("Artwork cache requires an absolute persistent DataPath.", nameof(dataPath));
            _path = Path.Combine(dataPath, "cache", "artwork");
            _jxl = jxl ?? new JxlArtworkCodec();
        }

        public async Task WarmAsync(LogicContext context, StorageFiles files, string rootPath, long libraryRootId, CancellationToken ct)
        {
            var (settings, candidates) = await context.ExecuteBatchAsync(async (ctx, token) =>
                (await ctx.ReadArtworkCacheSettingsAsync(token),
                    await ctx.ReadArtworkCacheCandidatesAsync(new ArtworkCacheRootData(libraryRootId), token)), ct);
            await Encoder.WaitAsync(ct);
            try
            {
                Directory.CreateDirectory(_path);
                var used = await PruneUnderLockAsync(context, settings.CacheLimitBytes, ct);
                if (settings.CacheLimitBytes == 0 || candidates.Count == 0) return;

                foreach (var candidate in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    if (used >= settings.CacheLimitBytes) break;
                    if (candidate.SourceSizeBytes is null or <= 0 or > MaxSourceBytes) continue;

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
                        var pixels = candidate.SourceKey.EndsWith(".jxl", StringComparison.OrdinalIgnoreCase)
                            ? await _jxl.DecodeAsync(source, ct)
                            : source;
                        if (pixels is null) continue;
                        var rendered = Encode(pixels, settings.CacheQualityId);
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

                        var result = new ArtworkRenditionSaveData(candidate.ArtworkId, candidate.WorkId, candidate.WorkUnitId, candidate.SlotId,
                            ArtworkFormat.Webp, false, value.Width, value.Height,
                            key, sourceHash, hash, value.WebpBytes.Length, settings.CacheQualityId, value.FallbackBytes,
                            value.FallbackBytes is { } fallbackBytes ? Convert.ToHexStringLower(SHA256.HashData(fallbackBytes)) : null);
                        await context.ExecuteBatchAsync(async (ctx, token) =>
                        {
                            await ctx.SaveArtworkRenditionAsync(result, token);
                            return true;
                        }, ct);

                        // Store the actual JXL as a distinct rendition. WebP is retained for all browsers
                        // that cannot decode image/jxl, even when JXL is the preferred cache format.
                        if (settings.CacheFormatId == ArtworkCacheFormat.Jxl)
                        {
                            var jxlBytes = await _jxl.EncodeAsync(value.PngBytes, settings.CacheQualityId, settings.ProgressiveJxl, ct);
                            if (jxlBytes is { Length: > 32 } && used + jxlBytes.Length <= settings.CacheLimitBytes)
                            {
                                var jxlHash = Convert.ToHexStringLower(SHA256.HashData(jxlBytes));
                                var jxlKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                                    sourceHash + ":jxl:" + (byte)settings.CacheQualityId + ":" + settings.ProgressiveJxl +
                                    ":" + value.Width + ":" + value.Height + ":cjxl-v1")));
                                var jxlPath = Path.Combine(_path, jxlKey + ".jxl");
                                if (!File.Exists(jxlPath))
                                {
                                    var partial = Path.Combine(_path, Guid.NewGuid().ToString("N") + ".partial");
                                    try
                                    {
                                        await File.WriteAllBytesAsync(partial, jxlBytes, ct);
                                        File.Move(partial, jxlPath, overwrite: false);
                                        used += jxlBytes.Length;
                                    }
                                    finally
                                    {
                                        if (File.Exists(partial)) File.Delete(partial);
                                    }
                                }

                                var jxlRendition = new ArtworkRenditionSaveData(candidate.ArtworkId, candidate.WorkId,
                                    candidate.WorkUnitId, candidate.SlotId, ArtworkFormat.Jxl, settings.ProgressiveJxl, value.Width, value.Height,
                                    jxlKey, sourceHash, jxlHash, jxlBytes.Length, settings.CacheQualityId, null, null);
                                await context.ExecuteBatchAsync(async (ctx, token) =>
                                {
                                    await ctx.SaveArtworkRenditionAsync(jxlRendition, token);
                                    return true;
                                }, ct);
                            }
                        }
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


        // The writer and pruner share one process-wide lock. Database references are
        // retired before files: the API falls back to PostgreSQL previews rather than
        // advertising a deleted JXL source.
        private async Task<long> PruneUnderLockAsync(LogicContext context, long limitBytes, CancellationToken ct)
        {
            var stored = await context.ExecuteBatchAsync(
                (ctx, token) => ctx.ReadArtworkStoredRenditionsAsync(token), ct);
            var indexed = stored.ToDictionary(item => item.CacheKey + Extension(item.FormatId), StringComparer.Ordinal);
            var files = Directory.EnumerateFiles(_path, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(info => IsManagedImage(info.Name) && (info.Attributes & FileAttributes.ReparsePoint) == 0)
                .ToDictionary(info => info.Name, StringComparer.Ordinal);
            long used = files.Values.Sum(info => info.Length);

            // Remove orphaned files first. Covers are content-addressed and can be shared.
            // Arbitrary files, symlinks and unrelated temporary files are never touched.
            foreach (var (name, info) in files.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                if (indexed.ContainsKey(name)) continue;
                if (TryDelete(info.FullName))
                {
                    used -= info.Length;
                    files.Remove(name);
                }
            }

            foreach (var item in stored)
            {
                ct.ThrowIfCancellationRequested();
                var name = item.CacheKey + Extension(item.FormatId);
                var isPresent = files.TryGetValue(name, out var info);
                if (isPresent && used <= limitBytes) continue;

                // A missing file also needs its DB entry retired so <picture> cannot
                // select a broken native source. The durable tiny fallback is retained.
                await context.ExecuteBatchAsync(
                    (ctx, token) => ctx.RetireArtworkCacheFileAsync(item, token), ct);
                if (isPresent && info is not null && TryDelete(info.FullName))
                {
                    used -= info.Length;
                    files.Remove(name);
                }
            }

            return used;
        }

        private static string Extension(ArtworkFormat format) => format == ArtworkFormat.Jxl ? ".jxl" : ".webp";

        private static bool IsManagedImage(string name)
        {
            if (name.Length is not (69 or 68)) return false;
            var extension = name.EndsWith(".webp", StringComparison.Ordinal) ? ".webp"
                : name.EndsWith(".jxl", StringComparison.Ordinal) ? ".jxl" : null;
            if (extension is null || name.Length != 64 + extension.Length) return false;
            return name.AsSpan(0, 64).ToString().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
        }

        private static bool TryDelete(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        // A cached response never opens the NAS or downloads provider images.
        public byte[]? Read(string? key, ArtworkFormat format = ArtworkFormat.Webp)
        {
            if (key is not { Length: 64 } || !key.All(char.IsAsciiHexDigit)) return null;
            if (format is not (ArtworkFormat.Webp or ArtworkFormat.Jxl)) return null;
            var path = Path.Combine(_path, key + (format == ArtworkFormat.Jxl ? ".jxl" : ".webp"));
            try
            {
                var info = new FileInfo(path);
                return info.Exists && info.Length is > 0 and <= 4194304 ? File.ReadAllBytes(path) : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static (int Width, int Height, byte[] WebpBytes, byte[]? FallbackBytes, byte[] PngBytes)? Encode(byte[] source, ArtworkQuality profile)
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
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            if (png is null) return null;
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
            return (width, height, webp.ToArray(), fallback, png.ToArray());
        }
    }
}
