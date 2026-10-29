namespace Jularr.Backend.Data.Enums.Media
{
    // How much of a provider's catalog Jularr keeps, for the whole instance: Lean keeps only the library and opened titles, Standard also Discover's lists,
    // Full the whole catalog of a catalog that has a full list.
    public enum CachingTier : byte
    {
        Lean = 1,
        Standard = 2,
        Full = 3
    }
}
