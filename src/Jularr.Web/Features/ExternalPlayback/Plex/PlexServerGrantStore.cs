using System.Text.Json;
using System.Text.Json.Serialization;
using Jularr.Web.Features.Providers;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Web.Features.ExternalPlayback.Plex;

/// <summary>A read-safe description of an explicitly approved Plex server.</summary>
public sealed record PlexSelectedServer(
    string MachineIdentifier,
    string Name,
    Uri Endpoint,
    IReadOnlyList<string> LibrarySectionIds,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Credential-bearing server grant: use it only inside trusted backend services.
/// The access token is encrypted on disk and never serialized or written into links.
/// </summary>
public sealed class PlexServerGrant(PlexSelectedServer server, string token)
{
    public PlexSelectedServer Server { get; } = server;

    [JsonIgnore]
    public string AccessToken { get; } = token;

    public override string ToString() =>
        $"PlexServerGrant {{ MachineIdentifier = {Server.MachineIdentifier} }}";
}

/// <summary>
/// Persists only owner-selected Plex resources. Discovery by itself never writes.
/// Tokens use the existing provider Data Protection purpose convention.
/// </summary>
public sealed class PlexServerGrantStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IDataProtector protector;
    private readonly TimeProvider clock;
    private readonly string filePath;

    public PlexServerGrantStore(
        IDataProtectionProvider protection,
        TimeProvider clock,
        string directory = "/data/integrations")
    {
        protector = ProviderCredentials.ProtectorFor(protection, "plex-server");
        this.clock = clock;
        filePath = Path.Combine(directory, "plex-servers.json");
    }

    public async Task<IReadOnlyList<PlexSelectedServer>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await LoadAsync(cancellationToken);
            return entries.Select(x => x.Server).ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Only trusted backend callers can retrieve server credentials.</summary>
    internal async Task<PlexServerGrant?> GetGrantAsync(
        string machineIdentifier,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await LoadAsync(cancellationToken);
            var entry = entries.SingleOrDefault(x =>
                string.Equals(x.Server.MachineIdentifier, machineIdentifier,
                    StringComparison.Ordinal));

            return entry is null
                ? null
                : new PlexServerGrant(entry.Server,
                    protector.Unprotect(entry.ProtectedAccessToken));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Select an exact server endpoint and libraries offered by Plex discovery.
    /// The caller must independently authorize the administrative selection.
    /// </summary>
    internal async Task SaveSelectedAsync(
        PlexServerCandidate discovered,
        Uri selectedEndpoint,
        IReadOnlyCollection<string> selectedLibraries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(selectedEndpoint);
        ArgumentNullException.ThrowIfNull(selectedLibraries);

        if (discovered.MachineIdentifier.Length is < 8 or > 160 ||
            !discovered.MachineIdentifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ||
            !IsSafeEndpoint(selectedEndpoint) ||
            !discovered.Connections.Any(x => x.Url == selectedEndpoint))
        {
            throw new ArgumentException(
                "The Plex server must be a verified discovered resource.",
                nameof(selectedEndpoint));
        }

        if (selectedLibraries.Count is < 1 or > 100 ||
            selectedLibraries.Any(x => string.IsNullOrWhiteSpace(x) ||
                x.Length > 12 || !x.All(char.IsAsciiDigit)))
        {
            throw new ArgumentException(
                "Select valid, non-empty Plex library identifiers.",
                nameof(selectedLibraries));
        }

        var token = discovered.AccessToken;
        if (token.Length is < 1 or > 2048 ||
            token.Any(c => c is <= ' ' or > '~'))
        {
            throw new ArgumentException(
                "The discovered Plex server grant is invalid.",
                nameof(discovered));
        }

        var selected = new PlexSelectedServer(
            discovered.MachineIdentifier,
            discovered.Name.Length <= 200 ? discovered.Name : discovered.Name[..200],
            selectedEndpoint,
            selectedLibraries.Distinct(StringComparer.Ordinal).OrderBy(x => x).ToArray(),
            clock.GetUtcNow());

        await gate.WaitAsync(cancellationToken);
        try
        {
            var entries = (await LoadAsync(cancellationToken)).ToList();
            entries.RemoveAll(x =>
                x.Server.MachineIdentifier == selected.MachineIdentifier);
            entries.Add(new PersistedGrant(
                selected,
                protector.Protect(token)));
            await WriteAsync(entries, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RemoveAsync(
        string machineIdentifier,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(machineIdentifier))
        {
            throw new ArgumentException(
                "A Plex server machine ID is required.",
                nameof(machineIdentifier));
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var entries = (await LoadAsync(cancellationToken)).ToList();
            if (entries.RemoveAll(x =>
                    x.Server.MachineIdentifier == machineIdentifier) > 0)
            {
                await WriteAsync(entries, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<PersistedGrant>> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        var entries = JsonSerializer.Deserialize<List<PersistedGrant>>(
            content, JsonOptions) ??
            throw new InvalidDataException("Plex server grants could not be read.");

        foreach (var entry in entries)
        {
            if (!IsSafeEndpoint(entry.Server.Endpoint) ||
                string.IsNullOrWhiteSpace(entry.ProtectedAccessToken) ||
                entry.Server.MachineIdentifier.Length is < 8 or > 160 ||
                entry.Server.LibrarySectionIds is not { Count: > 0 and <= 100 } ||
                entry.Server.LibrarySectionIds.Any(x => string.IsNullOrWhiteSpace(x) ||
                    x.Length > 12 || !x.All(char.IsAsciiDigit)))
            {
                throw new InvalidDataException(
                    "Stored Plex server grants contain invalid data.");
            }
        }

        return entries;
    }

    private Task WriteAsync(
        IReadOnlyList<PersistedGrant> entries,
        CancellationToken cancellationToken) =>
        ProviderCredentialFile.WriteAtomicAsync(
            filePath, JsonSerializer.Serialize(entries, JsonOptions),
            cancellationToken);

    private static bool IsSafeEndpoint(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 &&
        uri.Fragment.Length == 0;

    private sealed record PersistedGrant(
        PlexSelectedServer Server,
        string ProtectedAccessToken);
}
