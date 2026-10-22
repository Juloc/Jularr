namespace Jularr.Integrations.Data.Speech
{
    internal sealed class SpeechCatalogPackage(string key, string name, string language, string modelCard, string source, string sampleUrl, long sizeBytes, string sha256)
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        public string Language { get; } = language;
        public string ModelCard { get; } = modelCard;
        public string Source { get; } = source;
        public string SampleUrl { get; } = sampleUrl;
        public long SizeBytes { get; } = sizeBytes;
        public string Sha256 { get; } = sha256;
    }
}
