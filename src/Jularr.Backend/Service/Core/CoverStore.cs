using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Logic.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Core
{
    // An image answered from the cache of titles that are not Works: a file, or its bytes when nothing is kept.
    internal sealed record ExternalCoverFile(string? Path, byte[]? Bytes, string ContentType, DateTimeOffset LastModified, string ETag);

    // Provider images kept under the data folder like Sonarr's MediaCover: fetched once, kept as downloaded with a few widths beside them as JPEG XL (cjxl), WebP and JPEG, served only by Jularr.
    internal sealed partial class CoverStore(IOptions<MediaOptions> options, CoverClient client, MediaTranscoder transcoder, JxlEncoder jxl, ILogger<CoverStore> logger)
    {
        // Without a folder nothing is kept and the providers' own addresses stay in use.
        public bool IsEnabled => options.Value.CoversPath.Length > 0;

        public string Folder(long workId) => Path.Combine(options.Value.CoversPath, workId.ToString(CultureInfo.InvariantCulture));

        // After a Work's metadata was saved: its poster and backdrop are fetched when the provider's address is new.
        public async Task StoreAsync(LogicContext context, long workId, CancellationToken ct)
        {
            if (!IsEnabled)
            {
                return;
            }

            try
            {
                var targets = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadPendingCoversAsync(new WorkCoverQueryData(workId, 4), token), ct);
                await StoreAsync(context, targets, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A cover never fails the import it belongs to; the sweep tries again.
                logger.LogWarning(exception, "The covers of Work {WorkId} could not be stored.", workId);
            }
        }

        // Fetches each target, keeps it and records it; one that cannot be fetched waits a day.
        public async Task<int> StoreAsync(LogicContext context, IReadOnlyList<WorkCoverTargetData> targets, CancellationToken ct)
        {
            var stored = 0;
            foreach (var target in targets)
            {
                try
                {
                    var (bytes, contentType) = await client.DownloadAsync(target.SourceUrl, ct);
                    var version = Convert.ToHexStringLower(SHA256.HashData(bytes))[..12];
                    await WriteAsync(target, bytes, contentType, ct);
                    var data = new WorkCoverStoredData(target, contentType, version, CoverPaths.Url(target.WorkId, target.CoverKindId, target.WorkUnitId, version));
                    await context.ExecuteBatchAsync(async (ctx, token) =>
                    {
                        await ctx.SaveWorkCoverAsync(data, token);
                        return true;
                    }, ct);
                    stored++;
                }
                catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException
                    || exception is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    logger.LogInformation("A cover of Work {WorkId} could not be fetched: {Reason}", target.WorkId, exception.Message);
                    await context.ExecuteBatchAsync(async (ctx, token) =>
                    {
                        await ctx.SaveWorkCoverFailureAsync(target, token);
                        return true;
                    }, ct);
                }
            }

            return stored;
        }

        // The original as downloaded, then each width as JPEG XL, WebP and JPEG; a format the tools cannot make is left out and the next one answers for it, the original at last.
        private async Task WriteAsync(WorkCoverTargetData target, byte[] bytes, string contentType, CancellationToken ct)
        {
            var folder = Folder(target.WorkId);
            Directory.CreateDirectory(folder);
            var name = CoverPaths.Name(target.CoverKindId, target.WorkUnitId);
            var temporary = Path.Combine(folder, name + "." + Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, ct);
                foreach (var old in Directory.EnumerateFiles(folder, name + ".*").Concat(Directory.EnumerateFiles(folder, name + "-*.*")).Where(path => !path.Contains(".partial", StringComparison.Ordinal)))
                {
                    File.Delete(old);
                }

                var original = Path.Combine(folder, name + "." + CoverClient.Extension(contentType));
                File.Move(temporary, original, overwrite: true);
                foreach (var width in CoverPaths.Widths(target.CoverKindId))
                {
                    await ScaleAsync(original, Path.Combine(folder, name + "-" + width.ToString(CultureInfo.InvariantCulture)), width, ct);
                }
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        // One width as JPEG XL (when cjxl is there), WebP and JPEG next to each other, written aside and moved in whole.
        private async Task ScaleAsync(string original, string stem, int width, CancellationToken ct)
        {
            await ScaleJxlAsync(original, stem, width, ct);
            foreach (var extension in (string[])["webp", "jpg"])
            {
                var scaled = stem + "." + Guid.NewGuid().ToString("N") + ".partial." + extension;
                try
                {
                    if (await transcoder.ScaleImageAsync(original, scaled, width, ct))
                    {
                        File.Move(scaled, stem + "." + extension, overwrite: true);
                    }
                }
                finally
                {
                    File.Delete(scaled);
                }
            }
        }

        // JPEG XL is encoded from a lossless scaled copy, not from the JPEG, so the picture is compressed lossy only once.
        private async Task ScaleJxlAsync(string original, string stem, int width, CancellationToken ct)
        {
            if (!jxl.IsAvailable)
            {
                return;
            }

            var token = Guid.NewGuid().ToString("N");
            var lossless = stem + "." + token + ".partial.png";
            var encoded = stem + "." + token + ".partial.jxl";
            try
            {
                if (await transcoder.ScaleImageAsync(original, lossless, width, ct) && await jxl.EncodeAsync(lossless, encoded, ct))
                {
                    File.Move(encoded, stem + ".jxl", overwrite: true);
                }
            }
            finally
            {
                File.Delete(lossless);
                File.Delete(encoded);
            }
        }

        // The file that answers for a name and width: the stored width in the best format the client names (JPEG XL, WebP), else JPEG, else the original.
        public (string Path, string ContentType)? Find(long workId, CoverKind kind, long? workUnitId, int? width, string originalContentType, CoverFormats accepts)
        {
            var folder = Folder(workId);
            var name = CoverPaths.Name(kind, workUnitId);
            if (width is { } wanted)
            {
                var stem = Path.Combine(folder, name + "-" + wanted.ToString(CultureInfo.InvariantCulture));
                if (accepts.HasFlag(CoverFormats.Jxl) && File.Exists(stem + ".jxl"))
                {
                    return (stem + ".jxl", "image/jxl");
                }

                if (accepts.HasFlag(CoverFormats.WebP) && File.Exists(stem + ".webp"))
                {
                    return (stem + ".webp", "image/webp");
                }

                if (File.Exists(stem + ".jpg"))
                {
                    return (stem + ".jpg", "image/jpeg");
                }
            }

            var original = Path.Combine(folder, name + "." + CoverClient.Extension(originalContentType));
            return File.Exists(original) ? (original, originalContentType) : null;
        }

        // The cache for titles that are not Works: the provider's image is fetched when nobody asked for it yet, kept by its address, and every serve counts as use.
        private static readonly SemaphoreSlim[] ExternalGates = [.. Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1))];

        public async Task<ExternalCoverFile> ReadExternalAsync(string url, int? width, CoverFormats accepts, bool keeps, CancellationToken ct)
        {
            if (!CoverClient.IsProviderAddress(url) || width is { } wanted && !CoverPaths.ExternalWidths.Contains(wanted))
            {
                throw new KeyNotFoundException("The image is not available.");
            }

            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32];
            var folder = Path.Combine(options.Value.CoversPath, "external", key[..2]);
            var gate = ExternalGates[Convert.ToInt32(key[..2], 16)];
            await gate.WaitAsync(ct);
            try
            {
                var original = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, key + ".*").FirstOrDefault(path => !path.EndsWith(".partial", StringComparison.Ordinal)) : null;
                if (original is null)
                {
                    byte[] bytes;
                    string contentType;
                    try
                    {
                        (bytes, contentType) = await client.DownloadAsync(url, ct);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or InvalidDataException || exception is OperationCanceledException && !ct.IsCancellationRequested)
                    {
                        logger.LogInformation("A provider image could not be fetched: {Reason}", exception.Message);
                        throw new KeyNotFoundException("The image is not available.", exception);
                    }

                    // With nothing to keep, or no folder, the image is passed on as it comes.
                    if (!keeps || !IsEnabled)
                    {
                        return new ExternalCoverFile(null, bytes, contentType, DateTimeOffset.UtcNow, "\"" + key + "\"");
                    }

                    Directory.CreateDirectory(folder);
                    original = Path.Combine(folder, key + "." + CoverClient.Extension(contentType));
                    var temporary = original + "." + Guid.NewGuid().ToString("N") + ".partial";
                    await File.WriteAllBytesAsync(temporary, bytes, ct);
                    File.Move(temporary, original, overwrite: true);
                }

                // Use keeps an image: the time is renewed at most every ten minutes.
                if (File.GetLastWriteTimeUtc(original) < DateTime.UtcNow.AddMinutes(-10))
                {
                    File.SetLastWriteTimeUtc(original, DateTime.UtcNow);
                }

                var created = new DateTimeOffset(File.GetCreationTimeUtc(original), TimeSpan.Zero);
                if (width is { } size)
                {
                    var stem = Path.Combine(folder, key + "-" + size.ToString(CultureInfo.InvariantCulture));
                    if (!File.Exists(stem + ".webp") && !File.Exists(stem + ".jpg"))
                    {
                        await ScaleAsync(original, stem, size, ct);
                    }

                    if (accepts.HasFlag(CoverFormats.Jxl) && File.Exists(stem + ".jxl"))
                    {
                        return new ExternalCoverFile(stem + ".jxl", null, "image/jxl", created, "\"" + key + size.ToString(CultureInfo.InvariantCulture) + "x\"");
                    }

                    if (accepts.HasFlag(CoverFormats.WebP) && File.Exists(stem + ".webp"))
                    {
                        return new ExternalCoverFile(stem + ".webp", null, "image/webp", created, "\"" + key + size.ToString(CultureInfo.InvariantCulture) + "w\"");
                    }

                    if (File.Exists(stem + ".jpg"))
                    {
                        return new ExternalCoverFile(stem + ".jpg", null, "image/jpeg", created, "\"" + key + size.ToString(CultureInfo.InvariantCulture) + "\"");
                    }
                }

                return new ExternalCoverFile(original, null, CoverClient.ContentType(Path.GetExtension(original)), created, "\"" + key + "o\"");
            }
            finally
            {
                gate.Release();
            }
        }

        // Images nobody looked at for the days, then the least recently used above the size, leave; nothing at all is kept with a size of zero.
        public int TrimExternal(int keepDays, long maximumBytes)
        {
            var root = Path.Combine(options.Value.CoversPath, "external");
            if (!IsEnabled || !Directory.Exists(root))
            {
                return 0;
            }

            // One image is its original and the widths made from it, all named by the key.
            var entries = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .GroupBy(path => Path.GetFileName(path).Split('.', '-')[0], StringComparer.Ordinal)
                .Select(group => (Files: group.ToList(), Used: group.Max(File.GetLastWriteTimeUtc), Size: group.Sum(path => new FileInfo(path).Length)))
                .OrderBy(entry => entry.Used).ToList();
            var total = entries.Sum(entry => entry.Size);
            var removed = 0;
            foreach (var entry in entries)
            {
                if (entry.Used >= DateTime.UtcNow.AddDays(-keepDays) && total <= maximumBytes)
                {
                    continue;
                }

                foreach (var path in entry.Files)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning(exception, "A cached image could not be removed.");
                    }
                }

                total -= entry.Size;
                removed++;
            }

            return removed;
        }

        // A deleted Work takes its covers along.
        public void Delete(long workId)
        {
            try
            {
                if (IsEnabled && Directory.Exists(Folder(workId)))
                {
                    Directory.Delete(Folder(workId), true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "The covers of Work {WorkId} could not be removed.", workId);
            }
        }

        // Files no stored cover refers to: those of deleted Works, of replaced images and of interrupted writes.
        public async Task<int> SweepAsync(LogicContext context, CancellationToken ct)
        {
            if (!IsEnabled || !Directory.Exists(options.Value.CoversPath))
            {
                return 0;
            }

            var removed = 0;
            var folders = Directory.EnumerateDirectories(options.Value.CoversPath)
                .Select(path => (Path: path, Id: long.TryParse(global::System.IO.Path.GetFileName(path), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : (long?)null))
                .Where(folder => folder.Id is not null).ToList();
            foreach (var chunk in folders.Chunk(200))
            {
                var known = (await context.ExecuteBatchAsync((ctx, token) => ctx.ReadStoredCoversAsync(new WorkCoverWorkIdsData([.. chunk.Select(folder => folder.Id!.Value)]), token), ct))
                    .GroupBy(cover => cover.WorkId).ToDictionary(group => group.Key, group => group.Select(cover => CoverPaths.Name(cover.CoverKindId, cover.WorkUnitId)).ToHashSet(StringComparer.Ordinal));
                foreach (var folder in chunk)
                {
                    known.TryGetValue(folder.Id!.Value, out var names);
                    foreach (var file in Directory.EnumerateFiles(folder.Path))
                    {
                        var match = StoredFile().Match(global::System.IO.Path.GetFileName(file));
                        // A file just written may not be recorded yet.
                        var stale = !match.Success || names is null || !names.Contains(match.Groups[1].Value);
                        if (stale && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1))
                        {
                            File.Delete(file);
                            removed++;
                        }
                    }

                    if (!Directory.EnumerateFileSystemEntries(folder.Path).Any())
                    {
                        Directory.Delete(folder.Path);
                    }
                }
            }

            return removed;
        }

        [GeneratedRegex("^(poster|backdrop|unit-[0-9]+)(?:-[0-9]+)?\\.(?:jpg|png|gif|webp|avif|jxl)$", RegexOptions.CultureInvariant)]
        private static partial Regex StoredFile();
    }
}
