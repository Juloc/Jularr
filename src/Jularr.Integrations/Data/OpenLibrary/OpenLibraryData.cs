namespace Jularr.Integrations.Data.OpenLibrary
{
    internal enum OpenLibraryFailure : byte
    {
        Unavailable,
        NotFound,
        RateLimited,
        InvalidResponse
    }

    internal sealed class OpenLibraryException : Exception
    {
        public OpenLibraryException(OpenLibraryFailure failure, string message, Exception? innerException = null) : base(message, innerException)
        {
            Failure = failure;
        }

        public OpenLibraryFailure Failure { get; }
    }

    internal sealed class OpenLibraryResult
    {
        public OpenLibraryResult(string workId, string title, string[] authors, int? year, long? coverId, string? isbn, string[] languages)
        {
            WorkId = workId;
            Title = title;
            Authors = authors;
            Year = year;
            CoverId = coverId;
            Isbn = isbn;
            Languages = languages;
        }

        public string WorkId { get; }
        public string Title { get; }
        public string[] Authors { get; }
        public int? Year { get; }
        public long? CoverId { get; }
        public string? Isbn { get; }
        public string[] Languages { get; }
    }

    internal sealed class OpenLibraryWork
    {
        public OpenLibraryWork(string workId, string title, IReadOnlyList<string> authors, int? year, string? description, long? coverId)
        {
            WorkId = workId;
            Title = title;
            Authors = authors;
            Year = year;
            Description = description;
            CoverId = coverId;
        }

        public string WorkId { get; }
        public string Title { get; }
        public IReadOnlyList<string> Authors { get; }
        public int? Year { get; }
        public string? Description { get; }
        public long? CoverId { get; }
    }
}
