using System.Security.Claims;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Auth;

/// <summary>
/// What a profile may do with one <see cref="WorkMediaType"/>, as an ordered ladder (#436, epic #556).
/// Every higher rung includes the ones below it: <see cref="Instant"/> can also request and browse,
/// <see cref="Request"/> can also browse. This is the single vocabulary the request experience (#597),
/// the permission-derived shell (#598) and provider-driven discovery (#595) resolve against.
/// </summary>
public enum MediaCapability
{
    /// <summary>The media type does not exist for this profile: no navigation, no discovery, no API surface.</summary>
    Hidden = 0,

    /// <summary>The profile may discover and open the media type but may neither request nor add titles.</summary>
    Browse = 1,

    /// <summary>The profile may create an approval request; adding still waits for an approver.</summary>
    Request = 2,

    /// <summary>The profile may add/play the media type immediately, without waiting for approval.</summary>
    Instant = 3
}

/// <summary>Stable storage strings for <see cref="MediaCapability"/>. Never localise these; they are the JSON contract.</summary>
public static class MediaCapabilityNames
{
    public static string ToStorage(MediaCapability capability) => capability switch
    {
        MediaCapability.Hidden => "hidden",
        MediaCapability.Browse => "browse",
        MediaCapability.Request => "request",
        MediaCapability.Instant => "instant",
        _ => throw new ArgumentOutOfRangeException(nameof(capability))
    };

    public static MediaCapability Parse(string value) => value?.Trim().ToLowerInvariant() switch
    {
        "hidden" => MediaCapability.Hidden,
        "browse" => MediaCapability.Browse,
        "request" => MediaCapability.Request,
        "instant" => MediaCapability.Instant,
        _ => throw new ArgumentException($"Unknown media capability '{value}'.", nameof(value))
    };

    public static MediaCapability? TryParse(string? value) =>
        value is null ? null : Enum.GetValues<MediaCapability>()
            .Cast<MediaCapability?>()
            .FirstOrDefault(c => string.Equals(ToStorage(c!.Value), value.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Stable storage form of a role used as a policy key (Owner is never stored: it is unrestricted).</summary>
    public static string ToStorage(AccountRole role) => role.ToString();
}

/// <summary>
/// The owner's per-media-type capability policy: a default per configurable role, plus sparse per-user
/// overrides. This is the one canonical source of a profile's effective capability; features consume it
/// through <see cref="IMediaCapabilityService"/> instead of re-deriving rules.
/// </summary>
public sealed record MediaCapabilityPolicy(
    IReadOnlyDictionary<AccountRole, IReadOnlyDictionary<WorkMediaType, MediaCapability>> RoleDefaults,
    IReadOnlyDictionary<string, IReadOnlyDictionary<WorkMediaType, MediaCapability>> UserOverrides)
{
    /// <summary>Roles the matrix editor configures. <see cref="AccountRole.Owner"/> is always unrestricted and is not listed.</summary>
    public static IReadOnlyList<AccountRole> ConfigurableRoles { get; } =
        [AccountRole.MediaManager, AccountRole.User];

    /// <summary>
    /// The out-of-the-box policy: a media manager plays everything instantly; a plain user may see and
    /// request every media type but never adds without approval. The owner can narrow or widen this per
    /// role and per user afterwards.
    /// </summary>
    public static MediaCapabilityPolicy Default { get; } = new(
        new Dictionary<AccountRole, IReadOnlyDictionary<WorkMediaType, MediaCapability>>
        {
            [AccountRole.MediaManager] = FillAll(MediaCapability.Instant),
            [AccountRole.User] = FillAll(MediaCapability.Request)
        },
        new Dictionary<string, IReadOnlyDictionary<WorkMediaType, MediaCapability>>(StringComparer.Ordinal));

    /// <summary>The configured default for a role and media type, falling back to the built-in default.</summary>
    public MediaCapability RoleDefault(AccountRole role, WorkMediaType mediaType)
    {
        if (RoleDefaults.TryGetValue(role, out var map) && map.TryGetValue(mediaType, out var capability))
        {
            return capability;
        }

        return Default.RoleDefaults.TryGetValue(role, out var fallback)
            && fallback.TryGetValue(mediaType, out var fallbackCapability)
                ? fallbackCapability
                : MediaCapability.Hidden;
    }

    /// <summary>The explicit per-user override for a media type, or <c>null</c> when the user inherits the role default.</summary>
    public MediaCapability? UserOverride(string profileId, WorkMediaType mediaType) =>
        UserOverrides.TryGetValue(profileId, out var map) && map.TryGetValue(mediaType, out var capability)
            ? capability
            : null;

    /// <summary>
    /// The effective capability for a profile: the owner is unrestricted (<see cref="MediaCapability.Instant"/>);
    /// otherwise a per-user override wins over the role default. This is the resolution precedence.
    /// </summary>
    public MediaCapability Resolve(AccountRole role, string? profileId, WorkMediaType mediaType)
    {
        if (role == AccountRole.Owner)
        {
            return MediaCapability.Instant;
        }

        if (profileId is { Length: > 0 } && UserOverride(profileId, mediaType) is { } overridden)
        {
            return overridden;
        }

        return RoleDefault(role, mediaType);
    }

    public MediaCapabilityPolicy WithRoleDefault(AccountRole role, WorkMediaType mediaType, MediaCapability capability)
    {
        if (role == AccountRole.Owner)
        {
            throw new InvalidOperationException("The owner is unrestricted and has no configurable capability default.");
        }

        var roles = RoleDefaults.ToDictionary(pair => pair.Key, pair => CloneTypes(pair.Value));
        var map = roles.TryGetValue(role, out var existing)
            ? new Dictionary<WorkMediaType, MediaCapability>(existing)
            : new Dictionary<WorkMediaType, MediaCapability>();
        map[mediaType] = capability;
        roles[role] = map;
        return this with { RoleDefaults = roles.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<WorkMediaType, MediaCapability>)pair.Value) };
    }

    /// <summary>Sets or, when <paramref name="capability"/> is <c>null</c>, clears one per-user override.</summary>
    public MediaCapabilityPolicy WithUserOverride(string profileId, WorkMediaType mediaType, MediaCapability? capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var users = UserOverrides.ToDictionary(pair => pair.Key, pair => CloneTypes(pair.Value), StringComparer.Ordinal);
        var map = users.TryGetValue(profileId, out var existing)
            ? new Dictionary<WorkMediaType, MediaCapability>(existing)
            : new Dictionary<WorkMediaType, MediaCapability>();

        if (capability is { } value)
        {
            map[mediaType] = value;
        }
        else
        {
            map.Remove(mediaType);
        }

        if (map.Count == 0)
        {
            users.Remove(profileId);
        }
        else
        {
            users[profileId] = map;
        }

        return this with
        {
            UserOverrides = users.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<WorkMediaType, MediaCapability>)pair.Value,
                StringComparer.Ordinal)
        };
    }

    private static Dictionary<WorkMediaType, MediaCapability> CloneTypes(
        IReadOnlyDictionary<WorkMediaType, MediaCapability> source) => new(source);

    private static IReadOnlyDictionary<WorkMediaType, MediaCapability> FillAll(MediaCapability capability) =>
        WorkMediaTypes.All.ToDictionary(type => type, _ => capability);
}

/// <summary>
/// A profile's resolved capabilities across every media type — the snapshot the request experience,
/// permission-derived shell and discovery read. Build it once per request and reuse it.
/// </summary>
public sealed record MediaCapabilityView(
    bool IsOwner,
    IReadOnlyDictionary<WorkMediaType, MediaCapability> Capabilities)
{
    /// <summary>The effective capability for one media type (defaults to <see cref="MediaCapability.Hidden"/>).</summary>
    public MediaCapability Capability(WorkMediaType mediaType) =>
        Capabilities.TryGetValue(mediaType, out var capability) ? capability : MediaCapability.Hidden;

    /// <summary>Whether the profile reaches at least the required rung for this media type.</summary>
    public bool Allows(WorkMediaType mediaType, MediaCapability required) => Capability(mediaType) >= required;

    public bool CanBrowse(WorkMediaType mediaType) => Allows(mediaType, MediaCapability.Browse);

    /// <summary>Whether the Anime module runs; while it is off a title classified as Anime is just the Movie or Series it is.</summary>
    public bool AnimeModuleEnabled { get; init; } = true;

    /// <summary>Whether a Work may be browsed: a Movie or Series classified as Anime follows the Anime capability while the Anime module runs, and its own type otherwise.</summary>
    public bool CanBrowseWork(WorkMediaType technical, bool isAnime) =>
        isAnime && AnimeModuleEnabled && technical is WorkMediaType.Movie or WorkMediaType.Series ? CanBrowse(WorkMediaType.Anime) : CanBrowse(technical);

    public bool CanRequest(WorkMediaType mediaType) => Allows(mediaType, MediaCapability.Request);

    public bool CanUseInstantly(WorkMediaType mediaType) => Allows(mediaType, MediaCapability.Instant);

    /// <summary>The media types the profile may at least browse, in <see cref="WorkMediaTypes.All"/> order.</summary>
    public IReadOnlyList<WorkMediaType> VisibleMediaTypes { get; } =
        WorkMediaTypes.All.Where(type => Capabilities.TryGetValue(type, out var c) && c >= MediaCapability.Browse).ToArray();
}

/// <summary>Thrown when a server-side capability guard refuses an action for the current profile.</summary>
public sealed class MediaCapabilityDeniedException(WorkMediaType mediaType, MediaCapability required, MediaCapability actual)
    : Exception($"Media type {mediaType} requires capability {required} but the profile has {actual}.")
{
    public WorkMediaType MediaType { get; } = mediaType;
    public MediaCapability Required { get; } = required;
    public MediaCapability Actual { get; } = actual;
}

/// <summary>
/// Resolves a profile's effective per-media-type capability and gates actions by it. Owner is
/// unrestricted. This is the lynchpin interface consumed by #597 (request experience), #598
/// (permission-derived shell) and #595 (discovery categories).
/// </summary>
public interface IMediaCapabilityService
{
    /// <summary>The full resolved capability snapshot for a principal.</summary>
    Task<MediaCapabilityView> GetViewAsync(ClaimsPrincipal? user, CancellationToken cancellationToken = default);

    /// <summary>The effective capability of a principal for one media type.</summary>
    Task<MediaCapability> GetEffectiveCapabilityAsync(
        ClaimsPrincipal? user,
        WorkMediaType mediaType,
        CancellationToken cancellationToken = default);

    /// <summary>The media types a principal may at least browse (drives shell navigation and discovery rows).</summary>
    Task<IReadOnlyList<WorkMediaType>> GetVisibleMediaTypesAsync(
        ClaimsPrincipal? user,
        CancellationToken cancellationToken = default);

    /// <summary>Server-side guard: throws <see cref="MediaCapabilityDeniedException"/> when the profile is below <paramref name="required"/>.</summary>
    Task EnsureCapabilityAsync(
        ClaimsPrincipal? user,
        WorkMediaType mediaType,
        MediaCapability required,
        CancellationToken cancellationToken = default);
}
