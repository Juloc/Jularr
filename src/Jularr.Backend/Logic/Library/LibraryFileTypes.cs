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
        private static readonly FrozenSet<string> Ebook = FrozenSet.Create(".epub", ".pdf", ".mobi", ".azw3");
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

        public static bool IsProbeable(string relativePath) => GetAssetType(relativePath) is MediaAssetType.Video or MediaAssetType.Audio;

        public static MediaAssetType? GetAssetType(string relativePath)
        {
            var extension = Path.GetExtension(relativePath).ToLowerInvariant();
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
            ".mobi" or ".azw3" => header.Length >= 68 && header[60..68].SequenceEqual("BOOKMOBI"u8),
            _ => false
        };

        // The check a document fails, named for its text (content_epub, content_palmdoc, ...); null when it is what its name says.
        // An EPUB whose mimetype entry is not the first one is read from its ZIP directory; an old PalmDOC MOBI is named, since the reader cannot open it.
        public static async Task<string?> CheckDocumentAsync(string extension, Stream stream, CancellationToken ct)
        {
            var header = new byte[128];
            var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
            var format = extension.ToLowerInvariant();
            if (IsDocumentContent(format, header.AsSpan(0, read)))
            {
                return null;
            }

            if (format == ".epub" && read >= 4 && header.AsSpan(0, 4).SequenceEqual("PK\u0003\u0004"u8) && await IsEpubArchiveAsync(stream, ct))
            {
                return null;
            }

            return format == ".mobi" && read >= 68 && header.AsSpan(60, 8).SequenceEqual("TEXtREAd"u8) ? "content_palmdoc" : "content_" + format.TrimStart('.');
        }

        // A ZIP whose "mimetype" entry says application/epub+zip, or that has no such entry but a META-INF/container.xml.
        // Only the end record, the central directory (bounded) and the small mimetype entry are read; nothing is extracted.
        private static async Task<bool> IsEpubArchiveAsync(Stream stream, CancellationToken ct)
        {
            const int EndLength = 22;
            const int MaximumDirectoryBytes = 4 * 1024 * 1024;
            if (!stream.CanSeek || stream.Length < EndLength)
            {
                return false;
            }

            // The end record lies within the last 64 KiB and its own bytes.
            var tail = new byte[(int)Math.Min(stream.Length, EndLength + ushort.MaxValue)];
            stream.Position = stream.Length - tail.Length;
            await stream.ReadExactlyAsync(tail, ct);
            var end = tail.AsSpan().LastIndexOf("PK\u0005\u0006"u8);
            if (end < 0 || end + EndLength > tail.Length)
            {
                return false;
            }

            var entries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));
            if (entries > EpubReader.MaximumEntries || size > MaximumDirectoryBytes || offset + (long)size > stream.Length)
            {
                return false;
            }

            var directory = new byte[size];
            stream.Position = offset;
            await stream.ReadExactlyAsync(directory, ct);

            // The entries: the mimetype's place and size, and whether a container exists.
            var container = false;
            (ushort Method, uint Compressed, uint Length, uint Local)? mimetype = null;
            var position = 0;
            for (var index = 0; index < entries; index++)
            {
                if (position + 46 > directory.Length || !directory.AsSpan(position, 4).SequenceEqual("PK\u0001\u0002"u8))
                {
                    return false;
                }

                var entry = directory.AsSpan(position);
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[28..]);
                var next = position + 46 + nameLength + BinaryPrimitives.ReadUInt16LittleEndian(entry[30..]) + BinaryPrimitives.ReadUInt16LittleEndian(entry[32..]);
                if (next > directory.Length)
                {
                    return false;
                }

                var name = entry.Slice(46, nameLength);
                if (name.SequenceEqual("mimetype"u8))
                {
                    mimetype = (BinaryPrimitives.ReadUInt16LittleEndian(entry[10..]), BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]), BinaryPrimitives.ReadUInt32LittleEndian(entry[24..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(entry[42..]));
                }
                else if (name.SequenceEqual("META-INF/container.xml"u8))
                {
                    container = true;
                }

                position = next;
            }

            if (mimetype is not { } mime)
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
