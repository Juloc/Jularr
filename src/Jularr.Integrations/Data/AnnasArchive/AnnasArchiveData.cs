namespace Jularr.Integrations.Data.AnnasArchive
{
    internal enum AnnasArchiveFailure : byte
    {
        Unavailable,
        Refused,
        Gate,
        InvalidResponse,
        RateLimited,
        TimedOut,
        UntrustedOrigin
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
        public AnnasArchivePage(Uri? file, TimeSpan wait)
        {
            File = file;
            Wait = wait;
        }

        // Null while the page only counts down; the file names itself once the wait is over.
        public Uri? File { get; }
        public TimeSpan Wait { get; }
    }

    internal sealed class AnnasArchiveRecord
    {
        public AnnasArchiveRecord(string md5, string title, string? author, string extension, string? language, long? sizeBytes, Uri site, bool isPart = false)
        {
            Md5 = md5;
            Title = title;
            Author = author;
            Extension = extension;
            Language = language;
            SizeBytes = sizeBytes;
            Site = site;
            IsPart = isPart;
        }

        public string Md5 { get; }
        public string Title { get; }
        public string? Author { get; }
        public string Extension { get; }
        public string? Language { get; }
        public long? SizeBytes { get; }
        public Uri Site { get; }

        // The file is one part of a work; the whole work is elsewhere.
        public bool IsPart { get; }
    }

    // One wanted search shares successful answers across its title variants and configured mirrors.
    internal sealed class AnnasArchiveSearchContext : IDisposable
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, IReadOnlyList<AnnasArchiveRecord>> Answers { get; } = new(StringComparer.Ordinal);
        public HashSet<string> BlockedOrigins { get; } = new(StringComparer.Ordinal);
        public void Dispose() => Gate.Dispose();
    }
}
