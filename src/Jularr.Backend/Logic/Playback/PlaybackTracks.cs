using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jularr.Backend.Logic.Playback
{
    // Audio and subtitle choices: embedded streams by index, subtitle files beside the media by their stored file.
    internal static partial class PlaybackTracks
    {
        public const long MaximumSubtitleBytes = 10L * 1024 * 1024;

        private static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase) { "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text" };

        // ISO 639 codes, including the bibliographic forms subtitle files often use.
        private static readonly FrozenSet<string> LanguageCodes = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .SelectMany(culture => new[] { culture.TwoLetterISOLanguageName, culture.ThreeLetterISOLanguageName })
            .Concat(["alb", "arm", "baq", "bur", "chi", "cze", "dut", "fre", "geo", "ger", "gre", "ice", "mac", "mao", "may", "per", "rum", "slo", "tib", "wel"])
            .Where(code => code.Length is 2 or 3)
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        [GeneratedRegex(@"(\d{1,2}):(\d{2}):(\d{2}),(\d{3})")]
        private static partial Regex SrtTime();

        [GeneratedRegex(@"^([a-z]{2,3})(-[a-z]{2})?$", RegexOptions.IgnoreCase)]
        private static partial Regex LanguageTag();

        static PlaybackTracks()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public static string StreamKey(int streamIndex) => "s" + streamIndex.ToString(CultureInfo.InvariantCulture);

        public static string FileKey(long storedFileId) => "f" + storedFileId.ToString(CultureInfo.InvariantCulture);

        // "s3" names embedded stream 3, "f12" the subtitle file 12.
        public static (int? StreamIndex, long? StoredFileId) ParseKey(string key) =>
            key.Length > 1 && key[0] == 's' && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var stream) ? (stream, null)
            : key.Length > 1 && key[0] == 'f' && long.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var file) ? (null, file)
            : throw new KeyNotFoundException("The subtitle was not found.");

        // Image subtitles need burning in, which no plan offers yet.
        public static bool IsText(string? codec) => codec is not null && TextCodecs.Contains(codec);

        public static bool IsTextFile(string relativePath) => Path.GetExtension(relativePath).ToLowerInvariant() is ".srt" or ".vtt" or ".ass" or ".ssa";

        // "Movie.de.forced.srt" is German and forced; only real language codes count.
        public static (string? Language, bool IsForced) Describe(string relativePath)
        {
            var parts = Path.GetFileNameWithoutExtension(relativePath).Split('.');
            var tags = parts.Skip(1).Reverse().Take(3).Select(part => part.ToLowerInvariant()).ToArray();
            var language = tags.FirstOrDefault(tag => LanguageTag().Match(tag) is { Success: true } match && LanguageCodes.Contains(match.Groups[1].Value));
            return (language, tags.Contains("forced"));
        }

        // SubRip to WebVTT: a header, two-digit hours and dotted milliseconds.
        public static string SrtToVtt(string srt) =>
            "WEBVTT\n\n" + SrtTime().Replace(srt.Replace("\r\n", "\n").TrimStart('﻿'),
                match => match.Groups[1].Value.PadLeft(2, '0') + ":" + match.Groups[2].Value + ":" + match.Groups[3].Value + "." + match.Groups[4].Value);

        // A byte order mark decides; otherwise UTF-8, and older files are Windows-1252.
        public static string Decode(byte[] bytes)
        {
            if (bytes is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(1252).GetString(bytes);
            }
        }
    }
}
