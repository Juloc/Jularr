namespace Jularr.Integrations.Data.Media
{
    // A file carried inside a container (a font of a Matroska file, for instance), by its stream index.
    internal sealed class MediaAttachment
    {
        public MediaAttachment(int index, string? fileName, string? mimeType)
        {
            Index = index;
            FileName = fileName;
            MimeType = mimeType;
        }

        public int Index { get; }
        public string? FileName { get; }
        public string? MimeType { get; }
    }
}
