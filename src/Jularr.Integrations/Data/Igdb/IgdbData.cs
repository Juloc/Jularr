using Jularr.Integrations.Data.Common;

namespace Jularr.Integrations.Data.Igdb
{
    internal enum IgdbFailure : byte
    {
        Unavailable,
        Unauthorized,
        NotFound,
        RateLimited,
        InvalidResponse
    }

    internal sealed class IgdbException : Exception
    {
        public IgdbException(IgdbFailure failure, string message, Exception? innerException = null) : base(message, innerException)
        {
            Failure = failure;
        }

        public IgdbFailure Failure { get; }
    }

    // A Twitch developer application; the secret only goes to Twitch's token endpoint.
    internal sealed class IgdbConnection
    {
        public IgdbConnection(string clientId, string clientSecret)
        {
            ClientId = clientId;
            ClientSecret = clientSecret;
        }

        public string ClientId { get; }
        public string ClientSecret { get; }
    }

    internal sealed class IgdbPlatform
    {
        public IgdbPlatform(int id, string name, string? abbreviation)
        {
            Id = id;
            Name = name;
            Abbreviation = abbreviation;
        }

        public int Id { get; }
        public string Name { get; }
        public string? Abbreviation { get; }
    }

    internal sealed class IgdbGame
    {
        public IgdbGame(int id, string name, int? year, string? summary, string? coverImageId, IReadOnlyList<IgdbPlatform> platforms, IReadOnlyList<string> developers,
            IReadOnlyList<string> alternativeNames)
        {
            Id = id;
            Name = name;
            Year = year;
            Summary = summary;
            CoverImageId = coverImageId;
            Platforms = platforms;
            Developers = developers;
            AlternativeNames = alternativeNames;
        }

        public int Id { get; }
        public string Name { get; }
        public int? Year { get; }
        public string? Summary { get; }
        public string? CoverImageId { get; }
        public IReadOnlyList<IgdbPlatform> Platforms { get; }
        public IReadOnlyList<string> Developers { get; }
        public IReadOnlyList<string> AlternativeNames { get; }

        // The credited people with their ids at this provider.
        public IReadOnlyList<ProviderPerson> People { get; init; } = [];
    }
}
