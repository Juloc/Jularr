namespace Jularr.Integrations.Data.Translation
{
    // One file Mozilla publishes for its translations: a part of a language model or a build of the Bergamot engine.
    internal sealed class TranslationFile
    {
        public TranslationFile(string name, string version, string? fileType, string? fromLanguage, string? toLanguage, string hash, long size, string location, bool isFiltered)
        {
            Name = name;
            Version = version;
            FileType = fileType;
            FromLanguage = fromLanguage;
            ToLanguage = toLanguage;
            Hash = hash;
            Size = size;
            Location = location;
            IsFiltered = isFiltered;
        }

        public string Name { get; }
        public string Version { get; }

        // model, lex, vocab, srcvocab or trgvocab; none for the engine.
        public string? FileType { get; }
        public string? FromLanguage { get; }
        public string? ToLanguage { get; }

        // SHA-256 of the content, lower-case hex.
        public string Hash { get; }
        public long Size { get; }

        // Path below the attachment host.
        public string Location { get; }

        // Meant only for some Firefox builds, such as Android.
        public bool IsFiltered { get; }
    }

    internal sealed class TranslationSourceException(string message, Exception? innerException = null) : Exception(message, innerException);
}
