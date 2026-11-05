using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Core
{
    // A picture or page of a prepared book on disk: the file, its type and a tag of its content.
    internal sealed record ReadingFile(string FullPath, string MediaType, string ETag, DateTimeOffset LastModified);

    // Pictures and drawn pages of prepared books under the data folder, one folder per document; their text and translations stay in the database.
    // The Owner's reading cache bounds it: the books used longest ago leave first, a whole book at a time, and a book used in the last minutes stays.
    internal sealed partial class ReadingFiles(IOptions<MediaOptions> options, TimeProvider time)
    {
        // A book in use is never removed under its reader; its folder is touched at most this often.
        public static readonly TimeSpan InUse = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);
        private const string CompleteName = ".complete";

        private readonly Lock _gate = new();
        private readonly ConcurrentDictionary<long, DateTimeOffset> _touched = new();
        private long? _bytes;

        [GeneratedRegex("^[a-z0-9][a-z0-9.+-]{0,63}/[a-z0-9][a-z0-9.+-]{0,63}$")]
        private static partial Regex MediaTypePattern();

        public string Folder(long documentId) => Path.Combine(options.Value.ReadingCachePath, documentId.ToString(CultureInfo.InvariantCulture));

        // The kept file of a resource; a hit marks its book as used.
        public ReadingFile? Find(long documentId, string path)
        {
            var folder = Folder(documentId);
            var stem = Stem(path);
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, stem + ".*"))
                {
                    // <stem>.<content tag>.<media type with _ for />
                    var parts = Path.GetFileName(file).Split('.', 3);
                    if (parts.Length == 3 && parts[2].IndexOf('_') is > 0 and var slash)
                    {
                        Touch(documentId, folder);
                        return new ReadingFile(file, parts[2][..slash] + "/" + parts[2][(slash + 1)..], parts[1], File.GetLastWriteTimeUtc(file));
                    }
                }
            }
            catch (DirectoryNotFoundException)
            {
                // Nothing kept for this book.
            }

            return null;
        }

        // Written aside and moved in whole; a resource of another type than a plain media type is not kept.
        public async Task<ReadingFile?> WriteAsync(long documentId, string path, string mediaType, ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            mediaType = mediaType.ToLowerInvariant();
            if (!MediaTypePattern().IsMatch(mediaType))
            {
                return null;
            }

            var folder = Folder(documentId);
            Directory.CreateDirectory(folder);
            var stem = Stem(path);
            var tag = Convert.ToHexStringLower(SHA256.HashData(bytes.Span))[..16];
            var target = Path.Combine(folder, stem + "." + tag + "." + mediaType.Replace('/', '_'));
            var temporary = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, ct);
                foreach (var old in Directory.EnumerateFiles(folder, stem + ".*").Where(old => old != target))
                {
                    File.Delete(old);
                }

                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }

            lock (_gate)
            {
                _bytes += bytes.Length;
            }

            return new ReadingFile(target, mediaType, tag, File.GetLastWriteTimeUtc(target));
        }

        // Every picture a book draws on the server is kept: its next opening needs no drawing.
        public bool IsComplete(long documentId) => File.Exists(Path.Combine(Folder(documentId), CompleteName));

        public void Complete(long documentId)
        {
            var folder = Folder(documentId);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, CompleteName), []);
        }

        public void Delete(IEnumerable<long> documentIds)
        {
            lock (_gate)
            {
                foreach (var documentId in documentIds)
                {
                    _touched.TryRemove(documentId, out _);
                    try
                    {
                        Directory.Delete(Folder(documentId), true);
                    }
                    catch (DirectoryNotFoundException)
                    {
                        // Nothing was kept.
                    }
                    catch (IOException)
                    {
                        // A file still being written leaves with the next trim.
                    }
                }

                _bytes = null;
            }
        }

        // Within the budget again: the books used longest ago leave first; one in use stays, so the cache may run over by what is being read.
        public void Trim(long budgetBytes)
        {
            lock (_gate)
            {
                if (_bytes is { } known && known <= budgetBytes)
                {
                    return;
                }

                var root = new DirectoryInfo(options.Value.ReadingCachePath);
                if (!root.Exists)
                {
                    _bytes = 0;
                    return;
                }

                var inUseSince = time.GetUtcNow().UtcDateTime - InUse;
                var books = root.EnumerateDirectories()
                    .Select(folder => (Folder: folder, Used: folder.LastWriteTimeUtc, Bytes: folder.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)))
                    .OrderByDescending(book => book.Used)
                    .ToList();
                long kept = 0;
                foreach (var book in books)
                {
                    if (kept + book.Bytes <= budgetBytes || book.Used >= inUseSince)
                    {
                        kept += book.Bytes;
                        continue;
                    }

                    try
                    {
                        book.Folder.Delete(true);
                        if (long.TryParse(book.Folder.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var documentId))
                        {
                            _touched.TryRemove(documentId, out _);
                        }
                    }
                    catch (IOException)
                    {
                        kept += book.Bytes;
                    }
                }

                _bytes = kept;
            }
        }

        // A resource's file name stem from its path in the book, which may hold any character.
        private static string Stem(string path) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..32];

        private void Touch(long documentId, string folder)
        {
            var now = time.GetUtcNow();
            if (_touched.TryGetValue(documentId, out var last) && now - last < TouchEvery)
            {
                return;
            }

            _touched[documentId] = now;
            try
            {
                Directory.SetLastWriteTimeUtc(folder, now.UtcDateTime);
            }
            catch (IOException)
            {
                // A book removed meanwhile is drawn again when it is needed.
            }
        }
    }
}
