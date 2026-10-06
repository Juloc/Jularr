using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jularr.Web.Features.Providers;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Web.Features.Discovery;

public enum TmdbCredentialKind
{
    None,
    ReadAccessToken,
    ApiKey
}

/// <summary>Where the effective credential comes from; see <see cref="TmdbCredentialStore"/> for the precedence.</summary>
public enum TmdbCredentialSource
{
    None,
    Environment,
    Stored
}

public enum TmdbAvailability
{
    Ready,
    NotConfigured,
    Disabled
}

/// <summary>The plaintext credential, for the one adapter that signs TMDB requests. It never prints, serializes or logs its secret.</summary>
public sealed record TmdbCredential(TmdbCredentialKind Kind, [property: JsonIgnore] string Secret)
{
    public override string ToString() => $"TmdbCredential {{ Kind = {Kind} }}";
}

/// <summary>
/// The effective TMDB configuration. Only the adapter reads <see cref="Credential"/>; every view model works from
/// <see cref="TmdbSettingsStatus"/>, which carries no secret.
/// </summary>
public sealed record TmdbConfiguration(
    bool Enabled,
    TmdbCredentialSource Source,
    TmdbCredential? Credential,
    bool HasStoredReadAccessToken,
    bool HasStoredApiKey,
    bool StoredCredentialUnreadable,
    DateTimeOffset? UpdatedAtUtc)
{
    public bool HasStoredCredential => HasStoredReadAccessToken || HasStoredApiKey;

    public TmdbAvailability Availability => Credential is null ? TmdbAvailability.NotConfigured : Enabled ? TmdbAvailability.Ready : TmdbAvailability.Disabled;
}

/// <summary>An edit of the stored configuration. A null secret keeps the stored one, so unrelated edits never ask for it again.</summary>
public sealed record TmdbSettingsUpdate(bool Enabled, string? ReadAccessToken, string? ApiKey);

/// <summary>
/// The one owner of the TMDB credential. Non-secret settings live as JSON under <c>/data/integrations</c>; each secret is encrypted with the
/// provider protector (<see cref="ProviderCredentials.ProtectorFor"/>), following the OpenSubtitles convention.
/// <para>
/// Precedence: when <c>Providers:Tmdb:ReadAccessToken</c> or <c>Providers:Tmdb:ApiKey</c> is set in the deployment (environment or appsettings),
/// that credential is the effective one and the stored credential is ignored. The deployment is declarative infrastructure: a value an operator
/// pinned there must not be silently replaced by a database-like setting they cannot see, so the Admin UI shows it as externally managed instead
/// of letting a second source disagree. Without a deployment credential the stored one applies. Inside either source the Read Access Token wins
/// over the API key, and only that one credential is ever sent. The enabled switch is always the stored one: it is not a secret and the admin
/// may turn the provider off whoever supplies the credential.
/// </para>
/// </summary>
public sealed class TmdbCredentialStore
{
    public const string FileName = "tmdb.json";
    public const string DefaultDirectory = "/data/integrations";
    public const string ReadAccessTokenKey = "Providers:Tmdb:ReadAccessToken";
    public const string ApiKeyKey = "Providers:Tmdb:ApiKey";
    public const int MaxReadAccessTokenLength = 2048;
    public const int MaxApiKeyLength = 128;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IConfiguration configuration;
    private readonly IDataProtector protector;
    private readonly TimeProvider clock;
    private readonly ILogger<TmdbCredentialStore>? logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storePath;
    private StoredSettings? cached;

    public TmdbCredentialStore(IConfiguration configuration, IDataProtectionProvider dataProtectionProvider, TimeProvider clock, string directory = DefaultDirectory, ILogger<TmdbCredentialStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.configuration = configuration;
        this.clock = clock;
        this.logger = logger;
        protector = ProviderCredentials.ProtectorFor(dataProtectionProvider, ProviderKeys.Tmdb);
        storePath = Path.Combine(directory, FileName);
    }

    /// <summary>A secret as it may be stored and sent: visible ASCII only (no space, no control character) and within <paramref name="maxLength"/>.</summary>
    public static bool IsValidSecret(string? value, int maxLength) => !string.IsNullOrEmpty(value) && value.Length <= maxLength && value.All(character => character is > ' ' and <= '~');

    public async Task<TmdbConfiguration> GetAsync(CancellationToken cancellationToken)
    {
        StoredSettings stored;
        await gate.WaitAsync(cancellationToken);
        try
        {
            stored = cached ??= await LoadAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        var environmentToken = Clean(configuration[ReadAccessTokenKey]);
        var environmentKey = Clean(configuration[ApiKeyKey]);
        var hasStored = stored.ReadAccessToken is not null || stored.ApiKey is not null;
        var credential = (environmentToken, environmentKey, stored.ReadAccessToken, stored.ApiKey) switch
        {
            ({ } token, _, _, _) => new TmdbCredential(TmdbCredentialKind.ReadAccessToken, token),
            (_, { } key, _, _) => new TmdbCredential(TmdbCredentialKind.ApiKey, key),
            (_, _, { } token, _) => new TmdbCredential(TmdbCredentialKind.ReadAccessToken, token),
            (_, _, _, { } key) => new TmdbCredential(TmdbCredentialKind.ApiKey, key),
            _ => null
        };
        var source = environmentToken is not null || environmentKey is not null ? TmdbCredentialSource.Environment : hasStored ? TmdbCredentialSource.Stored : TmdbCredentialSource.None;
        return new TmdbConfiguration(stored.Enabled, source, credential, stored.ReadAccessToken is not null, stored.ApiKey is not null, stored.Unreadable, stored.UpdatedAtUtc);
    }

    /// <summary>Stores the enabled switch and any secret that is given; a secret that is not given stays as it is.</summary>
    public async Task SaveAsync(TmdbSettingsUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.ReadAccessToken is not null && !IsValidSecret(update.ReadAccessToken, MaxReadAccessTokenLength))
        {
            throw new ArgumentException("The TMDB Read Access Token is not valid.", nameof(update));
        }

        if (update.ApiKey is not null && !IsValidSecret(update.ApiKey, MaxApiKeyLength))
        {
            throw new ArgumentException("The TMDB API key is not valid.", nameof(update));
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = cached ??= await LoadAsync(cancellationToken);
            await WriteAsync(current with { Enabled = update.Enabled, ReadAccessToken = update.ReadAccessToken ?? current.ReadAccessToken, ApiKey = update.ApiKey ?? current.ApiKey, Unreadable = false }, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Removes the stored secrets, including ones that can no longer be decrypted, and keeps the enabled switch.</summary>
    public async Task RemoveCredentialAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = cached ??= await LoadAsync(cancellationToken);
            await WriteAsync(current with { ReadAccessToken = null, ApiKey = null, Unreadable = false }, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<StoredSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(storePath))
        {
            return StoredSettings.Empty;
        }

        PersistedSettings? persisted;
        try
        {
            persisted = JsonSerializer.Deserialize<PersistedSettings>(await File.ReadAllTextAsync(storePath, cancellationToken), JsonOptions);
        }
        catch (JsonException exception)
        {
            // An unreadable file must not take Discover down; the owner is told and enters the credential again.
            logger?.LogWarning(exception, "The stored TMDB settings contain invalid JSON.");
            return StoredSettings.Empty with { Unreadable = true };
        }

        if (persisted is null)
        {
            return StoredSettings.Empty;
        }

        try
        {
            return new StoredSettings(persisted.Enabled, persisted.UpdatedAtUtc, Unprotect(persisted.ProtectedReadAccessToken), Unprotect(persisted.ProtectedApiKey), false);
        }
        catch (CryptographicException exception)
        {
            // The Data Protection key ring changed: the secrets are unreadable, so the stored credential counts as absent and the owner re-enters it.
            logger?.LogWarning(exception, "Could not decrypt the stored TMDB credential.");
            return new StoredSettings(persisted.Enabled, persisted.UpdatedAtUtc, null, null, true);
        }
    }

    private string? Unprotect(string? protectedValue) => string.IsNullOrEmpty(protectedValue) ? null : protector.Unprotect(protectedValue);

    private async Task WriteAsync(StoredSettings settings, CancellationToken cancellationToken)
    {
        var updated = settings with { UpdatedAtUtc = clock.GetUtcNow() };
        var persisted = new PersistedSettings(
            updated.Enabled,
            updated.UpdatedAtUtc,
            updated.ReadAccessToken is null ? null : protector.Protect(updated.ReadAccessToken),
            updated.ApiKey is null ? null : protector.Protect(updated.ApiKey));
        await ProviderCredentialFile.WriteAtomicAsync(storePath, JsonSerializer.Serialize(persisted, JsonOptions), cancellationToken);
        cached = updated;
    }

    // What is on disk: the non-secret fields as plain JSON, each secret as a Data Protection blob.
    private sealed record PersistedSettings(bool Enabled, DateTimeOffset? UpdatedAtUtc, string? ProtectedReadAccessToken, string? ProtectedApiKey);

    private sealed record StoredSettings(bool Enabled, DateTimeOffset? UpdatedAtUtc, string? ReadAccessToken, string? ApiKey, bool Unreadable)
    {
        public static StoredSettings Empty { get; } = new(true, null, null, null, false);
    }
}
