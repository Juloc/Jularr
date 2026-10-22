namespace Jularr.Integrations.Data.Nyaa
{
    internal sealed class NyaaException : Exception
    {
        public NyaaException(string message, Exception? innerException = null) : base(message, innerException)
        {
        }
    }

    // One entry of the torrent index: a title, the number of its file and what the file holds.
    internal sealed class NyaaEntry
    {
        public NyaaEntry(string id, string title, string viewUri, string category, string categoryName, long sizeBytes, int seeders, DateTimeOffset published)
        {
            Id = id;
            Title = title;
            ViewUri = viewUri;
            Category = category;
            CategoryName = categoryName;
            SizeBytes = sizeBytes;
            Seeders = seeders;
            Published = published;
        }

        // The number of the entry; the file with the torrent is named after it.
        public string Id { get; }
        public string Title { get; }

        // The page of the entry; it shows what the file holds and the magnet to it.
        public string ViewUri { get; }

        // The category of the index; it names the language of the file.
        public string Category { get; }
        public string CategoryName { get; }
        public long SizeBytes { get; }
        public int Seeders { get; }
        public DateTimeOffset Published { get; }
    }
}
