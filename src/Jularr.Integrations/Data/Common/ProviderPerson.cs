namespace Jularr.Integrations.Data.Common
{
    // A person, band or studio as a provider credits them: their id there (some sources have none), the name, what they did and, for cast, whom they played.
    internal sealed class ProviderPerson
    {
        public const string Actor = "Actor";
        public const string GuestStar = "GuestStar";
        public const string Director = "Director";
        public const string Writer = "Writer";
        public const string Composer = "Composer";
        public const string Creator = "Creator";
        public const string Author = "Author";
        public const string Illustrator = "Illustrator";
        public const string Artist = "Artist";
        public const string Developer = "Developer";
        public const string Narrator = "Narrator";
        public const string Translator = "Translator";

        public ProviderPerson(string? id, string name, string role, string? credit = null, string? imageUrl = null)
        {
            Id = id;
            Name = name;
            Role = role;
            Credit = credit;
            ImageUrl = imageUrl;
        }

        public string? Id { get; }
        public string Name { get; }
        public string Role { get; }

        // The character played or the provider's own name for the job.
        public string? Credit { get; }
        public string? ImageUrl { get; }
    }
}
