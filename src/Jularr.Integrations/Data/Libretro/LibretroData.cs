namespace Jularr.Integrations.Data.Libretro
{
    internal sealed class LibretroException : Exception
    {
        public LibretroException(string message, Exception? innerException = null) : base(message, innerException)
        {
        }
    }

    // One platform's verified dumps.
    internal sealed class LibretroDat
    {
        public LibretroDat(string? version, IReadOnlyList<LibretroRom> roms)
        {
            Version = version;
            Roms = roms;
        }

        public string? Version { get; }
        public IReadOnlyList<LibretroRom> Roms { get; }
    }

    internal sealed class LibretroRom
    {
        public LibretroRom(string name, string sha1)
        {
            Name = name;
            Sha1 = sha1;
        }

        // The dump's game name, e.g. "Super Mario World (USA)".
        public string Name { get; }

        // Lowercase hex.
        public string Sha1 { get; }
    }
}
