namespace Jularr.Backend.Data.Enums.Media
{
    // How much of a provider's catalog Jularr keeps: Lean asks for Discover's lists each time, Standard keeps them, Full keeps the whole catalog.
    public enum CatalogCacheTier : byte
    {
        Lean = 1,
        Standard = 2,
        Full = 3
    }
}
