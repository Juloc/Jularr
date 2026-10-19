namespace Jularr.Integrations.Data.AnnasArchive
{
    internal enum AnnasArchiveFailure : byte
    {
        Unavailable,
        Refused,
        Gate,
        InvalidResponse
    }

    internal sealed class AnnasArchiveException : Exception
    {
        public AnnasArchiveException(AnnasArchiveFailure failure, string message, Exception? innerException = null) : base(message, innerException)
        {
            Failure = failure;
        }

        public AnnasArchiveFailure Failure { get; }
    }

    // One public slow download page: the file it names and the wait it asks for first.
    internal sealed class AnnasArchivePage
    {
        public AnnasArchivePage(Uri file, TimeSpan wait)
        {
            File = file;
            Wait = wait;
        }

        public Uri File { get; }
        public TimeSpan Wait { get; }
    }

    internal sealed class AnnasArchiveRecord
    {
        public AnnasArchiveRecord(string md5, string title, string? author, string extension, string? language, long? sizeBytes)
        {
            Md5 = md5;
            Title = title;
            Author = author;
            Extension = extension;
            Language = language;
            SizeBytes = sizeBytes;
        }

        public string Md5 { get; }
        public string Title { get; }
        public string? Author { get; }
        public string Extension { get; }
        public string? Language { get; }
        public long? SizeBytes { get; }
    }
}
