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
    // An image answered for a Work: a file, or its bytes when it was made for this one request.
    internal sealed record CoverFile(string? Path, byte[]? Bytes, string ContentType, DateTimeOffset LastModified);

    // An image answered from the cache of titles that are not Works: a file, or its bytes when nothing is kept.
    internal sealed record ExternalCoverFile(string? Path, byte[]? Bytes, string ContentType, DateTimeOffset LastModified, string ETag);

    // Provider images kept under the data folder like Sonarr's MediaCover: fetched once, kept as downloaded with a few widths beside them, each in the one format the Owner chose (JPEG XL by cjxl, else WebP), served only by Jularr.
    // A browser that does not take the stored format is answered with a copy made for that request alone.
    internal sealed partial class CoverStore(IOptions<MediaOptions> options, CoverClient client, MediaTranscoder transcoder, JxlEncoder jxl, ILogger<CoverStore> logger)
    {
        // A few conversions for browsers that do not take the stored format run beside each other, not a page's worth.
        private static readonly SemaphoreSlim DeriveGate = new(2, 2);

        // Without a folder nothing is kept and the providers' own addresses stay in use.
        public bool IsEnabled => options.Value.CoversPath.Length > 0;

        // Widths can be made at all; without ffmpeg only the downloads are kept.
        public bool CanScale => transcoder.IsAvailable;

        // The format widths are really kept in: the chosen one, JPEG XL only where cjxl runs.
        public CoverFormat EffectiveFormat(CoverFormat chosen) => chosen == CoverFormat.Jxl && !jxl.IsAvailable ? CoverFormat.WebP : chosen;

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
            if (targets.Count == 0)
            {
                return stored;
            }

            // The Owner's format, read once for the pass.
            var format = EffectiveFormat((await context.ExecuteBatchAsync((ctx, token) => ctx.ReadCoverCacheSettingsAsync(token), ct)).FormatId);
            foreach (var target in targets)
            {
                try
                {
                    var (bytes, contentType) = await client.DownloadAsync(target.SourceUrl, ct);
                    var version = Convert.ToHexStringLower(SHA256.HashData(bytes))[..12];
                    await WriteAsync(target, bytes, contentType, format, ct);
                    var data = new WorkCoverStoredData(target, contentType, version, CoverPaths.Url(target.WorkId, target.CoverKindId, target.WorkUnitId, version), format);
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

        // The original as downloaded, then each width in the one format; a width the tools cannot make in it is made in the next one, and without any the original answers.
        private async Task WriteAsync(WorkCoverTargetData target, byte[] bytes, string contentType, CoverFormat format, CancellationToken ct)
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
                    await ScaleAsync(original, Path.Combine(folder, name + "-" + width.ToString(CultureInfo.InvariantCulture)), width, format, ct);
                }
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        // One width in one file: written aside, its first bytes checked and moved in whole; only then do the files of the other formats of that width leave. Returns the extension made, none when no tool could.
        private async Task<string?> ScaleAsync(string original, string stem, int width, CoverFormat format, CancellationToken ct)
        {
            foreach (var extension in CoverPaths.Extensions(format))
            {
                if (extension == "jxl" && !jxl.IsAvailable)
                {
                    continue;
                }

                var scaled = stem + "." + Guid.NewGuid().ToString("N") + ".partial." + extension;
                try
                {
                    var made = extension == "jxl" ? await ScaleJxlAsync(original, scaled, width, ct) : await transcoder.ScaleImageAsync(original, scaled, width, ct);
                    if (made && IsFormat(scaled, extension))
                    {
                        File.Move(scaled, stem + "." + extension, overwrite: true);
                        RemoveOtherFormats(stem, extension);
                        return extension;
                    }
                }
                finally
                {
                    File.Delete(scaled);
                }
            }

            return null;
        }

        // JPEG XL is encoded from a lossless scaled copy, not from the JPEG, so the picture is compressed lossy only once.
        private async Task<bool> ScaleJxlAsync(string original, string output, int width, CancellationToken ct)
        {
            var lossless = output + ".lossless.png";
            try
            {
                return await transcoder.ScaleImageAsync(original, lossless, width, ct) && await jxl.EncodeAsync(lossless, output, ct);
            }
            finally
            {
                File.Delete(lossless);
            }
        }

        private static bool IsFormat(string path, string extension)
        {
            using var stream = File.OpenRead(path);
            var header = new byte[12];
            return CoverPaths.IsFormat(header.AsSpan(0, stream.ReadAtLeast(header, header.Length, false)), extension);
        }

        private void RemoveOtherFormats(string stem, string keep)
        {
            foreach (var format in CoverPaths.WidthFormats.Where(format => format.Extension != keep))
            {
                try
                {
                    File.Delete(stem + "." + format.Extension);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "A cover width in another format could not be removed.");
                }
            }
        }

        // The file that answers for a name and width: the stored width in the best format the client takes, a copy made for it when it takes none, else the original.
        public async Task<CoverFile?> FindAsync(long workId, CoverKind kind, long? workUnitId, int? width, string originalContentType, CoverFormats accepts, CancellationToken ct)
        {
            var folder = Folder(workId);
            var name = CoverPaths.Name(kind, workUnitId);
            var original = Path.Combine(folder, name + "." + CoverClient.Extension(originalContentType));
            if (width is { } wanted && await ChooseAsync(Path.Combine(folder, name + "-" + wanted.ToString(CultureInfo.InvariantCulture)), original, wanted, accepts, ct) is { } chosen)
            {
                return chosen;
            }

            return File.Exists(original) ? new CoverFile(original, null, originalContentType, Written(original)) : null;
        }

        // The stored width the client takes; when the only stored format is one it does not name, a WebP or JPEG made from the original for this answer and kept nowhere.
        private async Task<CoverFile?> ChooseAsync(string stem, string original, int width, CoverFormats accepts, CancellationToken ct)
        {
            var present = CoverPaths.WidthFormats.Where(format => File.Exists(stem + "." + format.Extension)).ToList();
            if (present.Count == 0)
            {
                return null;
            }

            var served = CoverPaths.Serve(present.Select(format => format.Extension), accepts);
            if (served is not null)
            {
                return new CoverFile(stem + "." + served, null, WidthContentType(served), Written(stem + "." + served));
            }

            var extension = CoverPaths.Derive(accepts);
            var bytes = File.Exists(original) ? await DeriveAsync(original, width, extension, ct) : null;
            return bytes is null ? null : new CoverFile(null, bytes, WidthContentType(extension), Written(original));
        }

        private async Task<byte[]?> DeriveAsync(string original, int width, string extension, CancellationToken ct)
        {
            await DeriveGate.WaitAsync(ct);
            var temporary = Path.Combine(Path.GetTempPath(), "jularr-cover-" + Guid.NewGuid().ToString("N") + "." + extension);
            try
            {
                return await transcoder.ScaleImageAsync(original, temporary, width, ct) && IsFormat(temporary, extension) ? await File.ReadAllBytesAsync(temporary, ct) : null;
            }
            finally
            {
                File.Delete(temporary);
                DeriveGate.Release();
            }
        }

        private static string WidthContentType(string extension) => CoverPaths.WidthFormats.First(format => format.Extension == extension).ContentType;

        private static DateTimeOffset Written(string path) => new(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);

        // The widths of one stored cover in this format, made again from its download; a width already in it only loses the files of other formats, and the download is never touched. False when it is gone or no tool could make a width.
        public async Task<bool> ConvertAsync(WorkCoverConvertData cover, CoverFormat format, CancellationToken ct)
        {
            var folder = Folder(cover.WorkId);
            var name = CoverPaths.Name(cover.CoverKindId, cover.WorkUnitId);
            var original = Path.Combine(folder, name + "." + CoverClient.Extension(cover.ContentType));
            if (!File.Exists(original))
            {
                return false;
            }

            var converted = true;
            foreach (var width in CoverPaths.Widths(cover.CoverKindId))
            {
                var stem = Path.Combine(folder, name + "-" + width.ToString(CultureInfo.InvariantCulture));
                var target = CoverPaths.Extensions(format)[0];
                if (File.Exists(stem + "." + target))
                {
                    RemoveOtherFormats(stem, target);
                }
                else
                {
                    converted &= await ScaleAsync(original, stem, width, format, ct) is not null;
                }
            }

            return converted;
        }

        // The cache of titles that are not Works: widths in another format than this one are made again from the cached download, this many at most; images nobody has the download of stay as they are.
        public async Task<int> ConvertExternalAsync(CoverFormat format, int limit, CancellationToken ct)
        {
            var root = Path.Combine(options.Value.CoversPath, "external");
            if (!IsEnabled || !Directory.Exists(root))
            {
                return 0;
            }

            var target = CoverPaths.Extensions(format)[0];
            var stale = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => (Path: path, Match: ExternalWidth().Match(Path.GetFileName(path))))
                .Where(item => item.Match.Success && item.Match.Groups[3].Value != target)
                .GroupBy(item => (Folder: Path.GetDirectoryName(item.Path)!, Key: item.Match.Groups[1].Value, Width: int.Parse(item.Match.Groups[2].ValueSpan, CultureInfo.InvariantCulture)));
            var converted = 0;
            foreach (var width in stale)
            {
                var gate = ExternalGates[Convert.ToInt32(width.Key.Key[..2], 16)];
                await gate.WaitAsync(ct);
                try
                {
                    var stem = Path.Combine(width.Key.Folder, width.Key.Key + "-" + width.Key.Width.ToString(CultureInfo.InvariantCulture));
                    var original = Directory.EnumerateFiles(width.Key.Folder, width.Key.Key + ".*").FirstOrDefault(path => !path.EndsWith(".partial", StringComparison.Ordinal));
                    if (File.Exists(stem + "." + target))
                    {
                        RemoveOtherFormats(stem, target);
                    }
                    else if (original is null || await ScaleAsync(original, stem, width.Key.Width, format, ct) is null)
                    {
                        continue;
                    }
                }
                finally
                {
                    gate.Release();
                }

                if (++converted >= limit)
                {
                    break;
                }
            }

            return converted;
        }

        // The cache for titles that are not Works: the provider's image is fetched when nobody asked for it yet, kept by its address, and every serve counts as use.
        private static readonly SemaphoreSlim[] ExternalGates = [.. Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1))];

        public async Task<ExternalCoverFile> ReadExternalAsync(string url, int? width, CoverFormats accepts, CoverFormat format, bool keeps, CancellationToken ct)
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
                    // A width nobody asked for yet is made in the Owner's format; one kept in another is converted by the backfill job.
                    var stem = Path.Combine(folder, key + "-" + size.ToString(CultureInfo.InvariantCulture));
                    if (!CoverPaths.WidthFormats.Any(stored => File.Exists(stem + "." + stored.Extension)))
                    {
                        await ScaleAsync(original, stem, size, EffectiveFormat(format), ct);
                    }

                    if (await ChooseAsync(stem, original, size, accepts, ct) is { } chosen)
                    {
                        return new ExternalCoverFile(chosen.Path, chosen.Bytes, chosen.ContentType, created, "\"" + key + size.ToString(CultureInfo.InvariantCulture) + CoverPaths.ETagSuffix(chosen.ContentType) + "\"");
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

        [GeneratedRegex("^([0-9a-f]{32})-([0-9]{3,4})\\.(jxl|webp|jpg)$", RegexOptions.CultureInvariant)]
        private static partial Regex ExternalWidth();
    }
}
