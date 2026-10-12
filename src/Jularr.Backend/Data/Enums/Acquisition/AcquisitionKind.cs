namespace Jularr.Backend.Data.Enums.Acquisition
{
    // How a Work is searched; Anime and Audiobook are acquisition kinds of the shared media types.
    internal enum AcquisitionKind : byte
    {
        Movie = 1,
        Tv = 2,
        Anime = 3,
        Book = 4,
        Audiobook = 5,
        Manga = 6,
        LightNovel = 7,
        Music = 8,
        Game = 9
    }
}
