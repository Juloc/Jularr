using System.Text.Json;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Auth;

/// <summary>
/// The one canonical store of the per-media-type capability policy (#436). It is durable-but-not-relational
/// configuration, so it uses the JSON settings-store pattern under <c>/data</c> (like
/// <c>AcquisitionAccessStore</c>'s policy half and <c>DownloadClientStore</c>) rather than an EF table.
/// </summary>
public sealed class MediaCapabilityStore
{
    public const string FileName = "media-capabilities.json";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path;

    public MediaCapabilityStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        path = Path.Combine(dataRoot, "auth", FileName);
    }

    public async Task<MediaCapabilityPolicy> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadUnlockedAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<MediaCapabilityPolicy> SaveAsync(
        MediaCapabilityPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        await gate.WaitAsync(cancellationToken);
        try
        {
            await SaveUnlockedAsync(policy, cancellationToken);
            return policy;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Sets one role default and persists the whole policy atomically.</summary>
    public Task<MediaCapabilityPolicy> SetRoleDefaultAsync(
        AccountRole role,
        WorkMediaType mediaType,
        MediaCapability capability,
        CancellationToken cancellationToken = default) =>
        MutateAsync(policy => policy.WithRoleDefault(role, mediaType, capability), cancellationToken);

    /// <summary>Sets or, when <paramref name="capability"/> is <c>null</c>, clears one per-user override.</summary>
    public Task<MediaCapabilityPolicy> SetUserOverrideAsync(
        string profileId,
        WorkMediaType mediaType,
        MediaCapability? capability,
        CancellationToken cancellationToken = default) =>
        MutateAsync(policy => policy.WithUserOverride(profileId, mediaType, capability), cancellationToken);

    public Task<MediaCapabilityPolicy> ConstrainNewExternalAccountAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        return MutateAsync(
            policy =>
            {
                var updated = policy;
                foreach (var mediaType in WorkMediaTypes.All)
                {
                    var effective = policy.Resolve(
                        AccountRole.User,
                        profileId,
                        mediaType);
                    var safe = effective > MediaCapability.Request
                        ? MediaCapability.Request
                        : effective;
                    updated = updated.WithUserOverride(
                        profileId,
                        mediaType,
                        safe);
                }

                return updated;
            },
            cancellationToken);
    }

    /// <summary>Removes every override for one profile (used when a user is deleted or reset to role defaults).</summary>
    public Task<MediaCapabilityPolicy> ClearUserAsync(string profileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return MutateAsync(
            policy =>
            {
                var users = policy.UserOverrides
                    .Where(pair => !string.Equals(pair.Key, profileId, StringComparison.Ordinal))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                return policy with { UserOverrides = users };
            },
            cancellationToken);
    }

    private async Task<MediaCapabilityPolicy> MutateAsync(
        Func<MediaCapabilityPolicy, MediaCapabilityPolicy> mutate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadUnlockedAsync(cancellationToken);
            var updated = mutate(current);
            await SaveUnlockedAsync(updated, cancellationToken);
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<MediaCapabilityPolicy> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return MediaCapabilityPolicy.Default;
        }

        PersistedPolicy? persisted;
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            persisted = JsonSerializer.Deserialize<PersistedPolicy>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Media capability policy '{path}' is invalid JSON.", exception);
        }

        if (persisted is null)
        {
            return MediaCapabilityPolicy.Default;
        }

        var roleDefaults = new Dictionary<AccountRole, IReadOnlyDictionary<WorkMediaType, MediaCapability>>();
        foreach (var (roleName, map) in persisted.RoleDefaults ?? [])
        {
            if (!Enum.TryParse<AccountRole>(roleName, out var role) || role == AccountRole.Owner)
            {
                // Unknown or non-configurable (Owner) roles are intentionally ignored.
                continue;
            }

            if (ReadTypeMap(map) is { Count: > 0 } types)
            {
                roleDefaults[role] = types;
            }
        }

        // Keep built-in defaults for any configurable role the file does not mention, so a fresh media
        // type or role always resolves to a sensible value without a runtime fallback branch elsewhere.
        foreach (var role in MediaCapabilityPolicy.ConfigurableRoles)
        {
            if (!roleDefaults.ContainsKey(role))
            {
                roleDefaults[role] = MediaCapabilityPolicy.Default.RoleDefaults[role];
            }
        }

        var userOverrides = new Dictionary<string, IReadOnlyDictionary<WorkMediaType, MediaCapability>>(StringComparer.Ordinal);
        foreach (var (profileId, map) in persisted.UserOverrides ?? [])
        {
            if (string.IsNullOrWhiteSpace(profileId))
            {
                continue;
            }

            if (ReadTypeMap(map) is { Count: > 0 } types)
            {
                userOverrides[profileId] = types;
            }
        }

        return new MediaCapabilityPolicy(roleDefaults, userOverrides);
    }

    private async Task SaveUnlockedAsync(MediaCapabilityPolicy policy, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var persisted = new PersistedPolicy(
            policy.RoleDefaults
                .Where(pair => pair.Key != AccountRole.Owner)
                .ToDictionary(
                    pair => MediaCapabilityNames.ToStorage(pair.Key),
                    pair => WriteTypeMap(pair.Value),
                    StringComparer.Ordinal),
            policy.UserOverrides.ToDictionary(
                pair => pair.Key,
                pair => WriteTypeMap(pair.Value),
                StringComparer.Ordinal));

        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(persisted, JsonOptions), cancellationToken);
            SetPrivateFileMode(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static IReadOnlyDictionary<WorkMediaType, MediaCapability> ReadTypeMap(Dictionary<string, string>? map)
    {
        var types = new Dictionary<WorkMediaType, MediaCapability>();
        if (map is null)
        {
            return types;
        }

        foreach (var (typeName, capabilityName) in map)
        {
            if (WorkMediaTypes.Parse(typeName) is { } type
                && MediaCapabilityNames.TryParse(capabilityName) is { } capability)
            {
                types[type] = capability;
            }
        }

        return types;
    }

    private static Dictionary<string, string> WriteTypeMap(IReadOnlyDictionary<WorkMediaType, MediaCapability> map) =>
        map.ToDictionary(
            pair => WorkMediaTypes.ToStorage(pair.Key),
            pair => MediaCapabilityNames.ToStorage(pair.Value),
            StringComparer.Ordinal);

    private static void SetPrivateFileMode(string filePath)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PersistedPolicy(
        Dictionary<string, Dictionary<string, string>> RoleDefaults,
        Dictionary<string, Dictionary<string, string>> UserOverrides);
}
