namespace Jularr.Integrations.Data.Servarr
{
    internal sealed class ServarrFormatCatalog
    {
        public ServarrFormatCatalog(IReadOnlyDictionary<int, string> languages, IReadOnlyDictionary<int, string> indexerFlags, IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> options)
        {
            Languages = languages;
            IndexerFlags = indexerFlags;
            Options = options;
        }

        public IReadOnlyDictionary<int, string> Languages { get; }
        public IReadOnlyDictionary<int, string> IndexerFlags { get; }
        public IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> Options { get; }

        // Protocol catalogs for pasted exports. A connected takeover reads its own server's catalogs instead.
        public static ServarrFormatCatalog Portable(bool movie)
        {
            var languageCodes = movie
                ? new[] { "unknown", "en", "fr", "es", "de", "it", "da", "nl", "ja", "is", "zh", "ru", "pl", "vi", "sv", "no", "fi", "tr", "pt", "nl", "el", "ko", "hu", "he", "lt", "cs", "hi", "ro", "th", "bg", "pt", "ar", "uk", "fa", "bn", "sk", "lv", "es", "ca", "hr", "sr", "bs", "et", "ta", "id", "te", "mk", "sl", "ml", "kn", "sq", "af", "mr", "tl", "ur", "rm", "mn", "ka" }
                : new[] { "unknown", "en", "fr", "es", "de", "it", "da", "nl", "ja", "is", "zh", "ru", "pl", "vi", "sv", "no", "fi", "tr", "pt", "nl", "el", "ko", "hu", "he", "lt", "cs", "ar", "hi", "bg", "ml", "uk", "sk", "th", "pt", "es", "ro", "lv", "fa", "ca", "hr", "sr", "bs", "et", "ta", "id", "mk", "sl", "az", "uz", "ms", "ur", "rm", "ka", "ga" };
            var languages = languageCodes.Select((code, id) => (code, id)).ToDictionary(item => item.id, item => item.code);
            languages[-2] = "original";
            if (movie) languages[-1] = "any";
            var sources = movie ? new[] { "unknown", "cam", "telesync", "telecine", "workprint", "dvd", "tv", "webdl", "webrip", "bluray" }
                : new[] { "unknown", "television", "televisionRaw", "web", "webRip", "dvd", "bluray", "blurayRaw" };
            var flags = movie ? new[] { "G_Freeleech", "G_Halfleech", "G_DoubleUpload", "PTP_Golden", "PTP_Approved", "G_Internal", "AHD_Internal", "G_Scene", "G_Freeleech75", "G_Freeleech25", "AHD_UserRelease", "Nuked" }
                : new[] { "Freeleech", "Halfleech", "DoubleUpload", "Internal", "Scene", "Freeleech75", "Freeleech25", "Nuked", "Subtitles" };
            var options = new Dictionary<string, IReadOnlyDictionary<int, string>>(StringComparer.Ordinal)
            {
                ["SourceSpecification/value"] = sources.Select((name, id) => (name, id)).ToDictionary(item => item.id, item => item.name),
                ["ResolutionSpecification/value"] = new[] { 0, 360, 480, 540, 576, 720, 1080, 2160, 4320 }.ToDictionary(value => value, value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ["ReleaseTypeSpecification/value"] = new Dictionary<int, string> { [0] = "unknown", [1] = "singleEpisode", [2] = "multiEpisode", [3] = "seasonPack" }
            };
            if (movie) options["QualityModifierSpecification/value"] = new[] { "none", "regional", "screener", "rawhd", "brdisk", "remux" }.Select((name, id) => (name, id)).ToDictionary(item => item.id, item => item.name);
            return new ServarrFormatCatalog(languages, flags.Select((name, index) => (name, id: 1 << index)).ToDictionary(item => item.id, item => item.name), options);
        }
    }
}
