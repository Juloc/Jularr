using System.Collections.Frozen;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Logic.Library
{
    internal static class LibraryFileTypes
    {
        private static readonly FrozenSet<string> Video = FrozenSet.Create(".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".webm", ".mpg", ".mpeg");
        private static readonly FrozenSet<string> Audio = FrozenSet.Create(".mp3", ".flac", ".m4a", ".m4b", ".ogg", ".opus", ".aac", ".wav", ".wv");
        private static readonly FrozenSet<string> Ebook = FrozenSet.Create(".epub", ".pdf", ".mobi", ".azw3");
        private static readonly FrozenSet<string> Comic = FrozenSet.Create(".cbz", ".cbr", ".cb7");
        private static readonly FrozenSet<string> Subtitle = FrozenSet.Create(".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup");
        private static readonly FrozenSet<string> Game = FrozenSet.Create(".iso", ".chd", ".cue", ".zip", ".7z", ".rar", ".nsp", ".xci", ".nds", ".3ds", ".gba", ".gb", ".gbc",
            ".nes", ".sfc", ".smc", ".n64", ".z64", ".gcm", ".rvz", ".wbfs");

        private static readonly FrozenDictionary<MediaType, FrozenSet<string>> ScanExtensions = new Dictionary<MediaType, FrozenSet<string>>
        {
            [MediaType.Movie] = Video.Union(Subtitle).ToFrozenSet(),
            [MediaType.Series] = Video.Union(Subtitle).ToFrozenSet(),
            [MediaType.Book] = Ebook.Union(Audio).ToFrozenSet(),
            [MediaType.Manga] = Comic.Union(Ebook).ToFrozenSet(),
            [MediaType.LightNovel] = Ebook,
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

        public static IReadOnlySet<string> GetScanExtensions(MediaType mediaType) => ScanExtensions[mediaType];

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

        // Ebooks are recognized by their bytes, not their names.
        public static bool IsEbookContent(string extension, ReadOnlySpan<byte> header) => extension.ToLowerInvariant() switch
        {
            ".epub" => header.Length >= 58 && header[..4].SequenceEqual("PK\u0003\u0004"u8) && header[30..38].SequenceEqual("mimetype"u8) && header.IndexOf("application/epub+zip"u8) > 0,
            ".pdf" => header.Length >= 5 && header[..5].SequenceEqual("%PDF-"u8),
            ".mobi" or ".azw3" => header.Length >= 68 && header[60..68].SequenceEqual("BOOKMOBI"u8),
            _ => false
        };
    }
}
