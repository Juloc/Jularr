namespace Jularr.Integrations.Data.Gutenberg
{
    internal sealed class GutenbergException : Exception
    {
        public GutenbergException(string message, Exception? innerException = null) : base(message, innerException)
        {
        }
    }

    internal sealed class GutenbergBook
    {
        public GutenbergBook(int id, string title, string[] authors, string[] languages)
        {
            Id = id;
            Title = title;
            Authors = authors;
            Languages = languages;
        }

        public int Id { get; }
        public string Title { get; }
        public string[] Authors { get; }
        public string[] Languages { get; }
    }
}
