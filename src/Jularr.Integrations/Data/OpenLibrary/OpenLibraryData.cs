using Jularr.Integrations.Data.Common;

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

        // The credited people with their ids at this provider.
        public IReadOnlyList<ProviderPerson> People { get; init; } = [];
    }

    // One printing of a work: its language (a MARC code such as "ger"), format, ISBNs, publisher and year; translators where named.
    internal sealed class OpenLibraryEdition
    {
        public OpenLibraryEdition(string editionId, string? title, string? language, string? physicalFormat, IReadOnlyList<string> isbns, string? publisher, int? year)
        {
            EditionId = editionId;
            Title = title;
            Language = language;
            PhysicalFormat = physicalFormat;
            Isbns = isbns;
            Publisher = publisher;
            Year = year;
        }

        public string EditionId { get; }
        public string? Title { get; }
        public string? Language { get; }
        public string? PhysicalFormat { get; }
        public IReadOnlyList<string> Isbns { get; }
        public string? Publisher { get; }
        public int? Year { get; }

        // Translators and readers as the edition names them, without ids.
        public IReadOnlyList<ProviderPerson> People { get; init; } = [];
    }
}
