using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.Core
{
    // The last warnings and errors of this process for the Owner; exceptions keep their type and message, never a stack trace.
    internal sealed class RecentLogs(TimeProvider time) : ILoggerProvider
    {
        private const int Capacity = 500;
        private readonly ConcurrentQueue<RecentLogEntry> _entries = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        // Newest first.
        public IReadOnlyList<RecentLogEntry> Read(int count) => [.. _entries.Reverse().Take(count)];

        public void Dispose()
        {
        }

        private void Add(RecentLogEntry entry)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity && _entries.TryDequeue(out _))
            {
            }
        }

        private sealed class Logger(RecentLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Warning and < LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                var message = formatter(state, exception);
                if (exception is not null) message += " (" + exception.GetType().Name + ": " + exception.Message + ")";
                owner.Add(new RecentLogEntry(owner.Now(), logLevel, category, message.Length > 2000 ? message[..2000] : message));
            }
        }

        private DateTime Now() => time.GetUtcNow().UtcDateTime;
    }

    internal sealed record RecentLogEntry(DateTime At, LogLevel Level, string Category, string Message);
}
