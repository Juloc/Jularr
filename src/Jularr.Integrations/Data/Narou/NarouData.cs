namespace Jularr.Integrations.Data.Narou
{
    internal enum NarouFailure : byte
    {
        // Narō did not answer or asked to slow down; a later attempt continues.
        Unavailable = 1,

        // The ncode or a chapter does not exist (any more).
        Missing = 2,

        // The answer is not what Narō's API or pages look like.
        InvalidResponse = 3,

        // The novel exceeds the chapter, text or file limits.
        TooLarge = 4
    }

    internal sealed class NarouException : Exception
    {
        public NarouException(NarouFailure failure, string message, Exception? innerException = null) : base(message, innerException)
        {
            Failure = failure;
        }

        public NarouFailure Failure { get; }
    }

    // One web novel as Narō's API describes it; the ncode is its stable identity.
    internal sealed class NarouNovel
    {
        public NarouNovel(string ncode, string title, string writer, string? story, int? firstYear, bool isShortStory, bool isFinished, int chapterCount)
        {
            Ncode = ncode;
            Title = title;
            Writer = writer;
            Story = story;
            FirstYear = firstYear;
            IsShortStory = isShortStory;
            IsFinished = isFinished;
            ChapterCount = chapterCount;
        }

        public string Ncode { get; }
        public string Title { get; }
        public string Writer { get; }
        public string? Story { get; }

        // The year of the first chapter.
        public int? FirstYear { get; }

        // A short story has its text on the novel's own page.
        public bool IsShortStory { get; }
        public bool IsFinished { get; }
        public int ChapterCount { get; }
    }

    // One chapter: its title and its paragraphs as safe XHTML.
    internal sealed class NarouChapter
    {
        public NarouChapter(int number, string title, string body)
        {
            Number = number;
            Title = title;
            Body = body;
        }

        public int Number { get; }
        public string Title { get; }
        public string Body { get; }
    }

    // How far a download got: chapters kept so far, all chapters, and whether the book was written.
    internal sealed class NarouProgress
    {
        public NarouProgress(int done, int total, bool isComplete, TimeSpan? retryAfter = null)
        {
            Done = done;
            Total = total;
            IsComplete = isComplete;
            RetryAfter = retryAfter;
        }

        public int Done { get; }
        public int Total { get; }
        public bool IsComplete { get; }

        // Narō failed for now; the kept chapters wait for the next attempt.
        public TimeSpan? RetryAfter { get; }
    }
}
