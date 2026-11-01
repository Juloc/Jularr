namespace Jularr.Backend.Service.Core
{
    // What the job "Book translations" is doing now, kept in memory for the Owner's progress view: the section it works on, how fast blocks and sections get
    // done, and why it does nothing. It starts empty after a restart; the counts of finished sections come from the database.
    internal sealed class BookTranslationProgress(TimeProvider time)
    {
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
        private readonly object _lock = new();
        private readonly Queue<DateTimeOffset> _blocks = new();
        private readonly Queue<DateTimeOffset> _sections = new();
        private BookTranslationUnit? _current;
        private DateTimeOffset _activityAt;
        private string? _issue;

        public void Picked(long documentId, int section, string language)
        {
            lock (_lock)
            {
                var unit = new BookTranslationUnit(documentId, section, language);
                if (unit == _current) return;
                _current = unit;
                _activityAt = time.GetUtcNow();
                _issue = null;
            }
        }

        public void BlockDone()
        {
            lock (_lock)
            {
                var now = time.GetUtcNow();
                _blocks.Enqueue(now);
                _activityAt = now;
                _issue = null;
            }
        }

        public void SectionDone()
        {
            lock (_lock)
            {
                _sections.Enqueue(time.GetUtcNow());
            }
        }

        // Nothing left to do or nothing that can be done: no unit is worked on.
        public void Idle(string? issueKey)
        {
            lock (_lock)
            {
                _current = null;
                _issue = issueKey;
            }
        }

        public BookTranslationSnapshot Snapshot()
        {
            lock (_lock)
            {
                var now = time.GetUtcNow();
                Trim(_blocks, now);
                Trim(_sections, now);
                var minutes = Window.TotalMinutes;
                return new BookTranslationSnapshot(_current, _current is null ? null : (int)Math.Max(0, (now - _activityAt).TotalSeconds), _issue,
                    Math.Round(_blocks.Count / minutes, 1), Math.Round(_sections.Count / minutes, 2));
            }
        }

        private static void Trim(Queue<DateTimeOffset> queue, DateTimeOffset now)
        {
            while (queue.Count > 0 && now - queue.Peek() > Window) queue.Dequeue();
        }
    }

    internal sealed record BookTranslationUnit(long DocumentId, int Section, string Language);

    internal sealed record BookTranslationSnapshot(BookTranslationUnit? Current, int? SecondsWithoutProgress, string? IssueKey, double BlocksPerMinute, double SectionsPerMinute);
}
