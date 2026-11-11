namespace Jularr.Backend.Service.Core
{
    // What the job "Book translations" is doing now, kept in memory for the Owner's progress view: the section it works on, how fast blocks get
    // done, and why it does nothing. It starts empty after a restart; the counts of finished sections come from the database ("BookTranslationProgress"),
    // which the job counts again for every book after a start and whenever the languages profiles read change, a batch per round.
    internal sealed class BookTranslationProgress(TimeProvider time)
    {
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
        private readonly object _lock = new();
        private readonly Queue<DateTimeOffset> _blocks = new();
        private BookTranslationUnit? _current;
        private DateTimeOffset _activityAt;
        private string? _issue;
        private string? _modelKey;
        private string? _digest;
        private string[]? _countedLanguages;
        private long? _countFrom = 1;

        // The document the full count goes on from for these languages, none once it went through every book; other languages than the count's start it over.
        public long? CountFrom(string[] languages)
        {
            lock (_lock)
            {
                if (_countedLanguages is null || !_countedLanguages.SequenceEqual(languages, StringComparer.Ordinal))
                {
                    _countedLanguages = languages;
                    _countFrom = 1;
                }

                return _countFrom;
            }
        }

        // A batch of the full count for these languages is done; next is where it goes on, none when it is complete.
        public void Counted(string[] languages, long? next)
        {
            lock (_lock)
            {
                if (_countedLanguages is not null && _countedLanguages.SequenceEqual(languages, StringComparer.Ordinal)) _countFrom = next;
            }
        }

        // The model (its cache key) and version the job translates with: only its complete sections count as finished.
        public void Translating(string modelKey, string digest)
        {
            lock (_lock)
            {
                _modelKey = modelKey;
                _digest = digest;
            }
        }

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
                var minutes = Window.TotalMinutes;
                return new BookTranslationSnapshot(_current, _current is null ? null : (int)Math.Max(0, (now - _activityAt).TotalSeconds), _issue,
                    Math.Round(_blocks.Count / minutes, 1), _modelKey, _digest, _countFrom is not null);
            }
        }

        private static void Trim(Queue<DateTimeOffset> queue, DateTimeOffset now)
        {
            while (queue.Count > 0 && now - queue.Peek() > Window) queue.Dequeue();
        }
    }

    internal sealed record BookTranslationUnit(long DocumentId, int Section, string Language);

    // Counting: the full count runs (after a start, or the languages changed), so the figures may still lag behind.
    internal sealed record BookTranslationSnapshot(BookTranslationUnit? Current, int? SecondsWithoutProgress, string? IssueKey, double BlocksPerMinute, string? ModelKey, string? Digest,
        bool Counting);
}
