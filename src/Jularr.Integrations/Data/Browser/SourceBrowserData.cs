namespace Jularr.Integrations.Data.Browser
{
    // A real browser reads what a plain request cannot: a page behind a browser check and the file it names.
    internal interface ISourceBrowser
    {
        // False when no browser binary is configured or found; the source then keeps its own way.
        bool IsAvailable { get; }

        // The rendered page, or null when the browser cannot answer it.
        Task<string?> ReadPageAsync(Uri uri, CancellationToken ct);

        // The file is written to the destination; the limit and the write reservation are the caller's rules.
        Task SaveFileAsync(Uri file, string destination, long? maximumBytes, Func<long, IDisposable>? reserveWrite, CancellationToken ct);
    }

    internal sealed class SourceBrowserException : Exception
    {
        public SourceBrowserException(string message, Exception? innerException = null) : base(message, innerException)
        {
        }
    }
}
