namespace Jularr.Integrations.Data.Browser
{
    // A real browser reads what a plain request cannot: a page behind a browser check and the file it names.
    internal interface ISourceBrowser
    {
        // False when no browser binary is configured or found; the source then keeps its own way.
        bool IsAvailable { get; }

        // What the browser is and what it did last, so an administrator can see and steer it.
        SourceBrowserStatus Status { get; }
        IReadOnlyList<SourceBrowserEvent> Events { get; }

        // The rendered page, or null when the browser cannot answer it.
        Task<string?> ReadPageAsync(Uri uri, CancellationToken ct);

        // Several addresses at the same time; the first that answers wins.
        Task<BrowserPage?> ReadPagesAsync(IReadOnlyList<Uri> addresses, CancellationToken ct);

        // The file is written to the destination; the limit and the write reservation are the caller's rules.
        Task SaveFileAsync(Uri file, string destination, long? maximumBytes, Func<long, IDisposable>? reserveWrite, CancellationToken ct);

        // The browser starts, stops and begins again as a new client.
        Task<SourceBrowserStatus> StartAsync(CancellationToken ct);
        Task StopAsync();
        Task ResetAsync();
    }

    internal sealed class SourceBrowserException : Exception
    {
        public SourceBrowserException(string message, Exception? innerException = null) : base(message, innerException)
        {
        }
    }

    // What the browser is and what it did last.
    internal sealed class SourceBrowserStatus
    {
        public SourceBrowserStatus(string? path, string? remoteAddress, string profilePath, bool isRunning, string? version, string? lastUrl, string? lastOutcome, string? lastError, DateTime? lastAttemptAt, int eventCount)
        {
            Path = path;
            RemoteAddress = remoteAddress;
            ProfilePath = profilePath;
            IsRunning = isRunning;
            Version = version;
            LastUrl = lastUrl;
            LastOutcome = lastOutcome;
            LastError = lastError;
            LastAttemptAt = lastAttemptAt;
            EventCount = eventCount;
        }

        public string? Path { get; }
        public string? RemoteAddress { get; }
        public string ProfilePath { get; }
        public bool IsRunning { get; }
        public string? Version { get; }
        public string? LastUrl { get; }
        public string? LastOutcome { get; }
        public string? LastError { get; }
        public DateTime? LastAttemptAt { get; }
        public int EventCount { get; }
    }

    // One thing the browser did.
    internal sealed class SourceBrowserEvent
    {
        public SourceBrowserEvent(DateTime at, string kind, string message)
        {
            At = at;
            Kind = kind;
            Message = message;
        }

        public DateTime At { get; }
        public string Kind { get; }
        public string Message { get; }
    }

    // One address that the browser read.
    internal sealed class BrowserPage
    {
        public BrowserPage(Uri address, string html)
        {
            Address = address;
            Html = html;
        }

        public Uri Address { get; }
        public string Html { get; }
    }
}
