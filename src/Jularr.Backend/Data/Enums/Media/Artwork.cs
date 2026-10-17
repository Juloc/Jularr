namespace Jularr.Backend.Data.Enums.Media
{
    // Common across every media type; seasons and episodes use the same slots as whole Works.
    public enum ArtworkSlot : byte
    {
        Poster = 1,
        Backdrop = 2,
        Logo = 3,
        Banner = 4,
        Thumbnail = 5
    }

    public enum ArtworkSourceKind : byte
    {
        Manual = 1,
        Sidecar = 2,
        Provider = 3,
        Embedded = 4
    }

    public enum ArtworkFormat : byte
    {
        Jpeg = 1,
        Png = 2,
        Webp = 3,
        Jxl = 4
    }

    public enum NasArtworkMode : byte
    {
        Compatible = 1,
        JxlAndCompatible = 2,
        JxlOnly = 3
    }

    public enum ArtworkCacheFormat : byte
    {
        Webp = 1,
        Jxl = 2
    }

    public enum ArtworkQuality : byte
    {
        Balanced = 1,
        High = 2,
        VisuallyLossless = 3,
        Lossless = 4
    }
}
