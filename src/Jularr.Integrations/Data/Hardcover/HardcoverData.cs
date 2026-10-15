namespace Jularr.Integrations.Data.Hardcover
{
    internal enum HardcoverFailure : byte
    {
        Unavailable,
        Unauthorized,
        RateLimited,
        InvalidResponse
    }

    internal sealed class HardcoverException : Exception
    {
        public HardcoverException(HardcoverFailure failure, string message, Exception? innerException = null) : base(message, innerException)
        {
            Failure = failure;
        }

        public HardcoverFailure Failure { get; }
    }

    // The account's API token; Hardcover issues it with its "Bearer" prefix, which may be left out.
    internal sealed class HardcoverConnection
    {
        public HardcoverConnection(string token)
        {
            Token = token;
        }

        public string Token { get; }
    }

    // A series a book belongs to and the book's place in it.
    internal sealed class HardcoverSeries
    {
        public HardcoverSeries(int id, string name, decimal? position)
        {
            Id = id;
            Name = name;
            Position = position;
        }

        public int Id { get; }
        public string Name { get; }
        public decimal? Position { get; }
    }
}
