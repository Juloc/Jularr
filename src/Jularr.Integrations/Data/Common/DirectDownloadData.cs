namespace Jularr.Integrations.Data.Common
{
    internal enum DirectDownloadFailure : byte
    {
        Unavailable,
        Unauthorized,
        Gate,
        TooLarge,
        ForbiddenRedirect,
        NotFound
    }

    internal sealed class DirectDownloadException : Exception
    {
        public DirectDownloadException(DirectDownloadFailure failure, string message, Exception? innerException = null) : base(message, innerException)
        {
            Failure = failure;
        }

        public DirectDownloadFailure Failure { get; }
    }

    // Credentials are sent to their own origin only, never to download hosts it redirects to.
    internal sealed class DirectCredentials
    {
        public DirectCredentials(Uri origin, string authorization)
        {
            Origin = origin;
            Authorization = authorization;
        }

        public Uri Origin { get; }
        public string Authorization { get; }

        public bool Covers(Uri uri) => Uri.Compare(uri, Origin, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
    }
}
