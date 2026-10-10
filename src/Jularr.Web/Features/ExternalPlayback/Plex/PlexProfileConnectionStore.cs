using System.Text.Json;
using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Providers;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Web.Features.ExternalPlayback.Plex;

/// <summary>
/// Stores a consented, independently verified Plex media connection per
/// Jularr profile. A Plex login identity is never itself a media connection.
/// Do not expose SaveVerifiedAsync to an untrusted HTTP handler: the caller
/// must validate the Plex PIN and bind it to the signed-in profile first.
/// </summary>
public sealed class PlexProfileConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IDataProtector protector;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string directory;

    public PlexProfileConnectionStore(
        IDataProtectionProvider protection,
        string directory = "/data/integrations/plex/profiles")
    {
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        protector = ProviderCredentials.ProtectorFor(protection, "plex-profile");
        this.directory = directory;
    }

    /// <summary>
    /// Called only after Jularr has verified the Plex user ID and its token,
    /// and the signed-in profile has explicitly opted in to media access.
    /// Automatic watchlist/progress sync is never enabled by connecting.
    /// </summary>
    public async Task SaveVerifiedAsync(
        string profileId,
        string plexUserId,
        string? plexUserName,
        string verifiedAccessToken,
        CancellationToken cancellationToken = default)
    {
        var path = PathFor(profileId);
        if (string.IsNullOrWhiteSpace(plexUserId) ||
            plexUserId.Length > 60 ||
            !plexUserId.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                "A verified stable Plex account identifier is required.",
                nameof(plexUserId));
        }

        if (string.IsNullOrWhiteSpace(verifiedAccessToken) ||
            verifiedAccessToken.Length > 2048 ||
            verifiedAccessToken.Any(c => c is <= ' ' or > '~'))
        {
            throw new ArgumentException(
                "A verified Plex media token is required.",
                nameof(verifiedAccessToken));
        }

        if (plexUserName?.Length > 160)
        {
            throw new ArgumentException(
                "The Plex account name is too long.", nameof(plexUserName));
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var entry = new PersistedConnection(
                plexUserId,
                plexUserName ?? "",
                protector.Protect(verifiedAccessToken),
                // A reconnected account must opt in to sync separately.
                false,
                DateTimeOffset.UtcNow);
            await ProviderCredentialFile.WriteAtomicAsync(
                path,
                JsonSerializer.Serialize(entry, JsonOptions),
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Safe for Settings UI: no access token or Plex server URL.</summary>
    public async Task<PlexProfileConnectionStatus?> GetStatusAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var path = PathFor(profileId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var entry = await ReadAsync(path, cancellationToken);
            if (entry is null)
            {
                return null;
            }

            var token = ProtectedSecrets.Read(
                protector, entry.ProtectedToken);
            var usable = !string.IsNullOrWhiteSpace(token);
            return new PlexProfileConnectionStatus(
                entry.PlexUserId,
                entry.PlexUserName,
                usable,
                usable && entry.SyncEnabled,
                entry.ConnectedAtUtc);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Credential-bearing internal helper. The caller must enforce the
    /// signed-in profile's ownership and current media access permissions.
    /// </summary>
    internal async Task<string?> GetBackendTokenAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var path = PathFor(profileId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var entry = await ReadAsync(path, cancellationToken);
            return entry is null
                ? null
                : ProtectedSecrets.Read(protector, entry.ProtectedToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Removes only the selected profile's Plex media grant. It never
    /// removes the Account-scoped Plex login identity or Jularr progress.
    /// </summary>
    public async Task<bool> DisconnectAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var path = PathFor(profileId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> DisconnectMatchingAsync(
        string profileId,
        string plexUserId,
        string verifiedAccessToken,
        CancellationToken cancellationToken = default)
    {
        var path = PathFor(profileId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var saved = await ReadAsync(path, cancellationToken);
            if (saved is null ||
                !string.Equals(saved.PlexUserId, plexUserId, StringComparison.Ordinal))
            {
                return false;
            }

            var token = ProtectedSecrets.Read(protector, saved.ProtectedToken);
            if (token is null ||
                !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(token),
                    System.Text.Encoding.UTF8.GetBytes(verifiedAccessToken)))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PersistedConnection?> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var entry = JsonSerializer.Deserialize<PersistedConnection>(
                await File.ReadAllTextAsync(path, cancellationToken),
                JsonOptions);
            if (entry is null ||
                string.IsNullOrWhiteSpace(entry.PlexUserId) ||
                entry.PlexUserId.Length > 60 ||
                !entry.PlexUserId.All(char.IsAsciiDigit) ||
                string.IsNullOrWhiteSpace(entry.ProtectedToken))
            {
                return null;
            }

            return entry;
        }
        catch (JsonException)
        {
            // Corrupt profile settings are disconnected, never trusted.
            return null;
        }
    }

    private string PathFor(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileId.Length > 80 ||
            profileId.Any(c =>
                !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
        {
            throw new ArgumentException(
                "The profile identifier is invalid.", nameof(profileId));
        }

        return Path.Combine(directory, profileId + ".json");
    }

    private sealed record PersistedConnection(
        string PlexUserId,
        string PlexUserName,
        string ProtectedToken,
        bool SyncEnabled,
        DateTimeOffset ConnectedAtUtc);
}

public sealed record PlexProfileConnectionStatus(
    string PlexUserId,
    string PlexUserName,
    bool IsUsable,
    bool SyncEnabled,
    DateTimeOffset ConnectedAtUtc);
