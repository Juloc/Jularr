using System.Text.Json.Serialization;

namespace Jularr.Integrations.Data.Opds
{
    internal sealed class OpdsConnection
    {
        public OpdsConnection(Uri catalog, string? username, string? password)
        {
            Catalog = catalog;
            Username = username;
            Password = password;
        }

        public Uri Catalog { get; }
        public string? Username { get; }

        [JsonIgnore]
        public string? Password { get; }
    }

    internal sealed class OpdsException : Exception
    {
        public OpdsException(string message, Exception? innerException = null) : base(message, innerException)
        {
        }
    }

    internal sealed class OpdsLink
    {
        public OpdsLink(Uri uri, string format)
        {
            Uri = uri;
            Format = format;
        }

        public Uri Uri { get; }

        // epub, pdf, mobi or azw3.
        public string Format { get; }
    }

    internal sealed class OpdsBook
    {
        public OpdsBook(string id, string title, string[] authors, string? language, IReadOnlyList<OpdsLink> links)
        {
            Id = id;
            Title = title;
            Authors = authors;
            Language = language;
            Links = links;
        }

        public string Id { get; }
        public string Title { get; }
        public string[] Authors { get; }
        public string? Language { get; }
        public IReadOnlyList<OpdsLink> Links { get; }
    }
}
