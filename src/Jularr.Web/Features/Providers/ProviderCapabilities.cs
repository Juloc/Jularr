namespace Jularr.Web.Features.Providers;

/// <summary>
/// What an external provider integration is able to do. A single integration
/// can advertise several capabilities (for example AniList offers both
/// metadata and a release schedule). New provider families (subtitles #560,
/// audiobooks #440) extend this set rather than inventing their own bespoke
/// capability flags.
/// </summary>
[Flags]
public enum ProviderCapabilities
{
    None = 0,

    /// <summary>Free-text or identifier search for release candidates (indexers).</summary>
    Search = 1 << 0,

    /// <summary>Work/edition metadata lookup (titles, relations, artwork ids).</summary>
    Metadata = 1 << 1,

    /// <summary>Forward-looking release/airing schedule.</summary>
    ReleaseSchedule = 1 << 2,

    /// <summary>Two-way progress or list synchronisation.</summary>
    Sync = 1 << 3,

    /// <summary>Handing an accepted release to a download client.</summary>
    Download = 1 << 4,

    /// <summary>Subtitle discovery/fetch (#560).</summary>
    Subtitles = 1 << 5,

    /// <summary>Audiobook discovery/fetch (reserved for #440).</summary>
    Audiobooks = 1 << 6
}

/// <summary>
/// The stable identity and advertised capabilities of one external provider
/// integration. <see cref="Key"/> is the lowercase, stable key every framework
/// component (rate limiter, health tracker, cache, executor) uses to scope its
/// per-provider state; keep it stable across releases.
/// </summary>
public sealed record ExternalProviderDescriptor(
    string Key,
    string DisplayName,
    ProviderCapabilities Capabilities)
{
    /// <summary>True when the provider advertises <paramref name="capability"/>.</summary>
    public bool Supports(ProviderCapabilities capability) =>
        (Capabilities & capability) == capability && capability != ProviderCapabilities.None;
}

/// <summary>
/// A provider integration that reports its own descriptor. Implementing this is
/// optional — providers can also be described purely through the
/// <see cref="ProviderCatalog"/> — but it lets a client carry its identity with
/// it (used by the migrated indexer and AniList clients).
/// </summary>
public interface IExternalProvider
{
    ExternalProviderDescriptor Descriptor { get; }
}

/// <summary>
/// Well-known provider keys. Kept in one place so the framework, the migrated
/// clients and the admin surface all agree on the exact strings.
/// </summary>
public static class ProviderKeys
{
    public const string AniList = "anilist";
    public const string Tmdb = "tmdb";
    public const string Tvdb = "tvdb";
    public const string Imdb = "imdb";
    public const string Newznab = "newznab";
    public const string Prowlarr = "prowlarr";
    public const string OpenSubtitles = "opensubtitles";
}
