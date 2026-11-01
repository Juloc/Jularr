using Jularr.Backend.Data.Common;

namespace Jularr.Backend.Data.Admin.Translation.V1
{
    public sealed class BookTranslationProgressParametersV1 : BaseParameters
    {
    }

    // Sections translated completely of all sections, in one language.
    public sealed class BookTranslationLanguageV1(string language, int done, int total)
    {
        public string Language { get; } = language;
        public int Done { get; } = done;
        public int Total { get; } = total;
    }

    // A book with its sections translated completely, over every language a profile reads.
    public sealed class BookTranslationBookV1(string title, int done, int total)
    {
        public string Title { get; } = title;
        public int Done { get; } = done;
        public int Total { get; } = total;
    }

    // The section the job works on now; Section counts from 1.
    public sealed class BookTranslationCurrentV1(string title, int section, string language, int secondsWithoutProgress)
    {
        public string Title { get; } = title;
        public int Section { get; } = section;
        public string Language { get; } = language;
        public int SecondsWithoutProgress { get; } = secondsWithoutProgress;
    }

    public sealed class BookTranslationProgressV1(BookTranslationLanguageV1[] languages, BookTranslationBookV1[] books, BookTranslationCurrentV1? current, double blocksPerMinute,
        long? secondsRemaining, string? issueKey) : BaseResponse
    {
        public BookTranslationLanguageV1[] Languages { get; } = languages;
        public BookTranslationBookV1[] Books { get; } = books;
        public BookTranslationCurrentV1? Current { get; } = current;
        public double BlocksPerMinute { get; } = blocksPerMinute;
        public long? SecondsRemaining { get; } = secondsRemaining;
        public string? IssueKey { get; } = issueKey;
    }
}
