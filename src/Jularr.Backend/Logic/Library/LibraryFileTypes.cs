using System.Buffers.Binary;
using System.Collections.Frozen;
using System.IO.Compression;
using System.Text;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Reading;

namespace Jularr.Backend.Logic.Library
{
    internal static class LibraryFileTypes
    {
        private static readonly FrozenSet<string> Video = FrozenSet.Create(".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".webm", ".mpg", ".mpeg");
        private static readonly FrozenSet<string> Audio = FrozenSet.Create(".mp3", ".flac", ".m4a", ".m4b", ".ogg", ".opus", ".aac", ".wav", ".wv");
        private static readonly FrozenSet<string> Ebook = FrozenSet.Create(".epub", ".pdf", ".mobi", ".azw3", ".azw", ".fb2", ".fb2.zip", ".txt", ".rtf", ".docx", ".djvu", ".djv");
        private static readonly FrozenSet<string> Comic = FrozenSet.Create(".cbz", ".cbr", ".cb7");
        // Kept equal to the generated column StoredFiles.IsSubtitle.
        private static readonly FrozenSet<string> Subtitle = FrozenSet.Create(".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup");
        private static readonly FrozenSet<string> Game = FrozenSet.Create(".iso", ".chd", ".cue", ".zip", ".7z", ".rar", ".nsp", ".xci", ".3ds", ".gcm", ".rvz", ".wbfs")
            .Union(GameRomMatching.FileExtensions).ToFrozenSet();

        private static readonly FrozenSet<string> MangaExtensions = Comic.Union(Ebook).ToFrozenSet();

        private static readonly FrozenDictionary<MediaType, FrozenSet<string>> ScanExtensions = new Dictionary<MediaType, FrozenSet<string>>
        {
            [MediaType.Movie] = Video.Union(Subtitle).ToFrozenSet(),
            [MediaType.Series] = Video.Union(Subtitle).ToFrozenSet(),
            [MediaType.Book] = Ebook.Union(Audio).Union(Comic).ToFrozenSet(),
            [MediaType.Music] = Audio,
            [MediaType.Game] = Game
        }.ToFrozenDictionary();

        private static readonly FrozenDictionary<string, (string Container, string ContentType)> Streams = new Dictionary<string, (string, string)>
        {
            [".mp4"] = ("mp4", "video/mp4"), [".m4v"] = ("mp4", "video/mp4"), [".mov"] = ("mp4", "video/quicktime"), [".webm"] = ("webm", "video/webm"),
            [".mkv"] = ("mkv", "video/x-matroska"), [".avi"] = ("avi", "video/x-msvideo"), [".wmv"] = ("wmv", "video/x-ms-wmv"), [".ts"] = ("ts", "video/mp2t"),
            [".m2ts"] = ("ts", "video/mp2t"), [".mpg"] = ("mpeg", "video/mpeg"), [".mpeg"] = ("mpeg", "video/mpeg"), [".mp3"] = ("mp3", "audio/mpeg"),
            [".m4a"] = ("mp4", "audio/mp4"), [".m4b"] = ("mp4", "audio/mp4"), [".flac"] = ("flac", "audio/flac"), [".ogg"] = ("ogg", "audio/ogg"),
            [".opus"] = ("ogg", "audio/ogg"), [".aac"] = ("aac", "audio/aac"), [".wav"] = ("wav", "audio/wav"), [".wv"] = ("wv", "audio/x-wavpack")
        }.ToFrozenDictionary();

        // Container family and HTTP content type of playable files.
        public static (string Container, string ContentType) GetStream(string relativePath) =>
            Streams.TryGetValue(Path.GetExtension(relativePath).ToLowerInvariant(), out var stream) ? stream : ("unknown", "application/octet-stream");

        public static IReadOnlySet<string> GetScanExtensions(MediaType mediaType, MediaSubtype? subtype) => subtype switch
        {
            MediaSubtype.Manga => MangaExtensions,
            MediaSubtype.LightNovel => Ebook,
            _ => ScanExtensions[mediaType]
        };

        // A game release is imported whole; installers and data files belong to it.
        public static IReadOnlySet<string>? GetImportExtensions(MediaType mediaType, MediaSubtype? subtype) => mediaType == MediaType.Game ? null : GetScanExtensions(mediaType, subtype);

        public static bool IsSubtitle(string relativePath) => Subtitle.Contains(Path.GetExtension(relativePath).ToLowerInvariant());

        // A file's extension in lower case; a zipped FictionBook keeps both parts.
        public static string Extension(string relativePath) =>
            relativePath.EndsWith(".fb2.zip", StringComparison.OrdinalIgnoreCase) ? ".fb2.zip" : Path.GetExtension(relativePath).ToLowerInvariant();

        // The format a file's name says (EPUB, FB2, ...): its extension without the dot, a zipped FictionBook as FB2.
        public static string FormatName(string relativePath) => Extension(relativePath).TrimStart('.').Split('.')[0].ToUpperInvariant();

        public static bool IsProbeable(string relativePath) => GetAssetType(relativePath) is MediaAssetType.Video or MediaAssetType.Audio;

        public static MediaAssetType? GetAssetType(string relativePath)
        {
            var extension = Extension(relativePath);
            if (Video.Contains(extension)) return MediaAssetType.Video;
            if (Audio.Contains(extension)) return MediaAssetType.Audio;
            if (Comic.Contains(extension)) return MediaAssetType.Comic;
            if (Ebook.Contains(extension)) return MediaAssetType.Ebook;
            return Game.Contains(extension) ? MediaAssetType.Game : null;
        }

        // Ebooks and comic archives are recognized by their bytes, not their names.
        public static bool IsDocumentContent(string extension, ReadOnlySpan<byte> header) => extension.ToLowerInvariant() switch
        {
            ".cbz" => header.Length >= 4 && header[..4].SequenceEqual("PK\u0003\u0004"u8),
            ".cbr" => header.Length >= 6 && header[..6].SequenceEqual("Rar!\u001A\u0007"u8),
            ".cb7" => header.Length >= 6 && header[..6].SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }),
            ".epub" => header.Length >= 58 && header[..4].SequenceEqual("PK\u0003\u0004"u8) && header[30..38].SequenceEqual("mimetype"u8) && header.IndexOf("application/epub+zip"u8) > 0,
            ".pdf" => header.Length >= 5 && header[..5].SequenceEqual("%PDF-"u8),
            ".mobi" => header.Length >= 68 && (header[60..68].SequenceEqual("BOOKMOBI"u8) || header[60..68].SequenceEqual("TEXtREAd"u8)),
            ".azw" or ".azw3" => header.Length >= 68 && header[60..68].SequenceEqual("BOOKMOBI"u8),
            ".fb2" => IsFictionBook(header),
            ".rtf" => header.Length >= 5 && header[..5].SequenceEqual("{\\rtf"u8),
            ".txt" => IsPlainText(header),
            ".djvu" or ".djv" => IsDjvu(header),
            _ => false
        };

        // The check a document fails, named for its text (content_epub, content_fb2, ...); null when it is what its name says.
        // A protected Kindle book passes: it is a book, which the reader says it cannot open.
        public static async Task<string?> CheckDocumentAsync(string extension, Stream stream, CancellationToken ct)
        {
            var header = new byte[1024];
            var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
            var format = extension.ToLowerInvariant();
            if (IsDocumentContent(format, header.AsSpan(0, read)))
            {
                return null;
            }

            // An EPUB whose mimetype entry is not the first one, a DOCX and a zipped FB2 are read from their ZIP directory.
            if (format is ".epub" or ".docx" or ".fb2.zip" && read >= 4 && header.AsSpan(0, 4).SequenceEqual("PK\u0003\u0004"u8) && await ReadZipDirectoryAsync(stream, ct) is { } entries)
            {
                var valid = format switch
                {
                    ".epub" => await IsEpubArchiveAsync(stream, entries, ct),
                    ".docx" => entries.Any(entry => entry.Name == "word/document.xml"),
                    _ => entries.Any(entry => entry.Name.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase) && entry.Length > 0)
                };
                if (valid)
                {
                    return null;
                }
            }

            return "content_" + format switch { ".fb2.zip" => "fb2", ".djv" => "djvu", _ => format.TrimStart('.') };
        }

        // A DjVu document is an IFF file: "AT&T", then a FORM chunk of one page (DJVU) or of several (DJVM).
        private static bool IsDjvu(ReadOnlySpan<byte> header) =>
            header.Length >= 16 && header[..8].SequenceEqual("AT&TFORM"u8) && (header[12..16].SequenceEqual("DJVU"u8) || header[12..16].SequenceEqual("DJVM"u8));

        // A FictionBook is XML whose root is FictionBook, in UTF-8, a single-byte code page or UTF-16.
        private static bool IsFictionBook(ReadOnlySpan<byte> header)
        {
            var text = header is [0xFF, 0xFE, ..] ? Encoding.Unicode.GetString(header[2..]) : header is [0xFE, 0xFF, ..] ? Encoding.BigEndianUnicode.GetString(header[2..]) : Encoding.Latin1.GetString(header);
            var start = text.TrimStart('\uFEFF', '\u00EF', '\u00BB', '\u00BF', ' ', '\t', '\r', '\n');
            return start.StartsWith('<') && text.Contains("<FictionBook", StringComparison.Ordinal);
        }

        // Text has no zero bytes and few other control characters (UTF-16 says so by its byte order mark).
        private static bool IsPlainText(ReadOnlySpan<byte> header)
        {
            if (header is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
            {
                return header.Length >= 4;
            }

            var control = 0;
            foreach (var value in header)
            {
                if (value == 0) return false;
                if (value < 0x20 && value is not (0x09 or 0x0A or 0x0C or 0x0D or 0x1A)) control++;
            }

            return header.Length > 0 && control * 20 <= header.Length;
        }

        private sealed record ZipEntry(string Name, ushort Method, uint Compressed, uint Length, uint Local);

        // The entries of a ZIP from its end record and its central directory (bounded); nothing is extracted. Null when it is no readable ZIP.
        private static async Task<IReadOnlyList<ZipEntry>?> ReadZipDirectoryAsync(Stream stream, CancellationToken ct)
        {
            const int EndLength = 22;
            const int MaximumDirectoryBytes = 4 * 1024 * 1024;
            if (!stream.CanSeek || stream.Length < EndLength)
            {
                return null;
            }

            // The end record lies within the last 64 KiB and its own bytes.
            var tail = new byte[(int)Math.Min(stream.Length, EndLength + ushort.MaxValue)];
            stream.Position = stream.Length - tail.Length;
            await stream.ReadExactlyAsync(tail, ct);
            var end = tail.AsSpan().LastIndexOf("PK\u0005\u0006"u8);
            if (end < 0 || end + EndLength > tail.Length)
            {
                return null;
            }

            var count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));
            if (count > EpubReader.MaximumEntries || size > MaximumDirectoryBytes || offset + (long)size > stream.Length)
            {
                return null;
            }

            var directory = new byte[size];
            stream.Position = offset;
            await stream.ReadExactlyAsync(directory, ct);
            var entries = new List<ZipEntry>(count);
            var position = 0;
            for (var index = 0; index < count; index++)
            {
                if (position + 46 > directory.Length || !directory.AsSpan(position, 4).SequenceEqual("PK\u0001\u0002"u8))
                {
                    return null;
                }

                var entry = directory.AsSpan(position);
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[28..]);
                var next = position + 46 + nameLength + BinaryPrimitives.ReadUInt16LittleEndian(entry[30..]) + BinaryPrimitives.ReadUInt16LittleEndian(entry[32..]);
                if (next > directory.Length)
                {
                    return null;
                }

                entries.Add(new ZipEntry(Encoding.UTF8.GetString(entry.Slice(46, nameLength)), BinaryPrimitives.ReadUInt16LittleEndian(entry[10..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]), BinaryPrimitives.ReadUInt32LittleEndian(entry[24..]), BinaryPrimitives.ReadUInt32LittleEndian(entry[42..])));
                position = next;
            }

            return entries;
        }

        // A ZIP whose "mimetype" entry says application/epub+zip, or that has no such entry but a META-INF/container.xml; only the small mimetype entry is read.
        private static async Task<bool> IsEpubArchiveAsync(Stream stream, IReadOnlyList<ZipEntry> entries, CancellationToken ct)
        {
            var container = entries.Any(entry => entry.Name == "META-INF/container.xml");
            if (entries.FirstOrDefault(entry => entry.Name == "mimetype") is not { } mime)
            {
                return container;
            }

            // The mimetype entry is a few bytes, stored or deflated.
            if (mime.Length > 128 || mime.Compressed > 1024 || mime.Method is not (0 or 8) || mime.Local + 30L > stream.Length)
            {
                return false;
            }

            var local = new byte[30];
            stream.Position = mime.Local;
            await stream.ReadExactlyAsync(local, ct);
            var start = mime.Local + 30L + BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(26)) + BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(28));
            if (!local.AsSpan(0, 4).SequenceEqual("PK\u0003\u0004"u8) || start + mime.Compressed > stream.Length)
            {
                return false;
            }

            var packed = new byte[mime.Compressed];
            stream.Position = start;
            await stream.ReadExactlyAsync(packed, ct);
            var text = new byte[129];
            int length;
            if (mime.Method == 0)
            {
                length = Math.Min(packed.Length, text.Length);
                packed.AsSpan(0, length).CopyTo(text);
            }
            else
            {
                try
                {
                    await using var inflated = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
                    length = await inflated.ReadAtLeastAsync(text, text.Length, throwOnEndOfStream: false, ct);
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            }

            return length <= 128 && Encoding.ASCII.GetString(text, 0, length).Trim() == "application/epub+zip";
        }
    }
}
