using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Providers;

namespace Jularr.Web.Features.Discovery;

public enum TmdbTestOutcome
{
    Succeeded,
    AuthenticationFailed,
    Unreachable,
    RateLimited
}

/// <summary>
/// TMDB as a provider of the shared Provider UI: the schema it is rendered from (two masked fields, its capabilities, its state), and the save, test and
/// remove operations behind the form. Every change re-evaluates the provider from scratch: its observed health is forgotten and Discover forgets the
/// answers it remembered, so the next load is judged by the new configuration only.
/// </summary>
public sealed class TmdbSettingsService(
    TmdbCredentialStore store,
    TmdbDiscoveryProvider provider,
    ProviderHealthTracker health,
    ProviderResponseCache cache,
    DiscoverySourceFlights flights,
    IInstanceModuleService modules) : IProviderSettings
{
    public const string TokenField = "readAccessToken";
    public const string ApiKeyField = "apiKey";

    private static readonly string[] CapabilityKeys =
    [
        "admin.providers.capability.movies",
        "admin.providers.capability.series",
        "admin.providers.capability.metadata",
        "admin.providers.capability.artwork",
        "admin.providers.capability.externalIds"
    ];

    public string Key => ProviderKeys.Tmdb;

    public async Task<ProviderView> GetViewAsync(CancellationToken cancellationToken)
    {
        var configuration = await store.GetAsync(cancellationToken);
        var snapshot = health.Get(ProviderKeys.Tmdb);
        var instance = await modules.GetAsync(cancellationToken);
        var required = instance.IsEnabled(InstanceModule.Movie) || instance.IsEnabled(InstanceModule.Tv);
        var state = configuration.Availability switch
        {
            TmdbAvailability.NotConfigured => ProviderConnectionState.NotConfigured,
            TmdbAvailability.Disabled => ProviderConnectionState.Disabled,
            _ => snapshot.Status switch
            {
                ProviderHealthStatus.Healthy => ProviderConnectionState.Healthy,
                ProviderHealthStatus.Degraded => ProviderConnectionState.Degraded,
                ProviderHealthStatus.AuthenticationFailed => ProviderConnectionState.AuthenticationFailed,
                ProviderHealthStatus.Unavailable => ProviderConnectionState.Unavailable,
                _ => ProviderConnectionState.Unknown
            }
        };

        var blocks = required && state is ProviderConnectionState.NotConfigured or ProviderConnectionState.Disabled or ProviderConnectionState.AuthenticationFailed;
        return new ProviderView(
            ProviderKeys.Tmdb,
            "TMDB",
            "admin.providers.summary.tmdb",
            ProviderFamily.Metadata,
            configuration.Enabled,
            state,
            required ? "admin.providers.requiredReason" : null,
            CapabilityKeys,
            [
                new ProviderSecretField(TokenField, "admin.providers.field.readAccessToken", "admin.providers.field.readAccessTokenHint", TmdbCredentialStore.MaxReadAccessTokenLength, configuration.HasStoredReadAccessToken),
                new ProviderSecretField(ApiKeyField, "admin.providers.field.apiKey", "admin.providers.field.apiKeyHint", TmdbCredentialStore.MaxApiKeyLength, configuration.HasStoredApiKey)
            ],
            configuration.Source == TmdbCredentialSource.Environment,
            configuration.HasStoredCredential,
            configuration.StoredCredentialUnreadable,
            configuration.Source switch
            {
                TmdbCredentialSource.Environment => "admin.providers.fact.sourceEnvironment",
                TmdbCredentialSource.Stored => "admin.providers.fact.sourceStored",
                _ => "admin.providers.fact.sourceNone"
            },
            // The executor counts any answer, a refusal included, as a success; a refused credential has no successful call to show.
            snapshot.Status == ProviderHealthStatus.AuthenticationFailed ? null : snapshot.LastSuccessUtc,
            snapshot.LastFailureUtc,
            blocks ? new ProviderBlocking("setup.provider.blocked.title", "setup.provider.blocked.body", "setup.provider.blocked.configure", "setup.provider.blocked.disable") : null);
    }

    public async Task<ProviderFeedback> SaveAsync(bool enabled, IReadOnlyDictionary<string, string?> secrets, CancellationToken cancellationToken)
    {
        var token = Given(secrets, TokenField);
        var key = Given(secrets, ApiKeyField);
        if (HasInvalid(token, key))
        {
            return ProviderFeedback.InvalidInput;
        }

        await store.SaveAsync(new TmdbSettingsUpdate(enabled, token, key), cancellationToken);
        Reevaluate();
        return ProviderFeedback.Saved;
    }

    /// <summary>
    /// Tests a credential with one real call. Typing a Read Access Token or an API key tests that unsaved value (and never touches the provider's
    /// health); leaving both empty tests the credential that is in effect, which does record health.
    /// </summary>
    public async Task<ProviderFeedback> TestAsync(IReadOnlyDictionary<string, string?> secrets, CancellationToken cancellationToken)
    {
        var token = Given(secrets, TokenField);
        var key = Given(secrets, ApiKeyField);
        TmdbCredential? credential;
        var recordHealth = false;
        if (token is null && key is null)
        {
            credential = (await store.GetAsync(cancellationToken)).Credential;
            recordHealth = true;
        }
        else if (HasInvalid(token, key))
        {
            return ProviderFeedback.InvalidInput;
        }
        else
        {
            credential = token is not null ? new TmdbCredential(TmdbCredentialKind.ReadAccessToken, token) : new TmdbCredential(TmdbCredentialKind.ApiKey, key!);
        }

        if (credential is null)
        {
            return ProviderFeedback.TestNotConfigured;
        }

        return await provider.TestConnectionAsync(credential, recordHealth, cancellationToken) switch
        {
            TmdbTestOutcome.Succeeded => ProviderFeedback.TestSucceeded,
            TmdbTestOutcome.AuthenticationFailed => ProviderFeedback.TestAuthenticationFailed,
            TmdbTestOutcome.RateLimited => ProviderFeedback.TestRateLimited,
            _ => ProviderFeedback.TestUnreachable
        };
    }

    public async Task RemoveSavedValuesAsync(CancellationToken cancellationToken)
    {
        await store.RemoveCredentialAsync(cancellationToken);
        Reevaluate();
    }

    /// <summary>Movie and TV are turned off; their data stays, and enabling them again restores them.</summary>
    public async Task DisableDependentFeaturesAsync(CancellationToken cancellationToken)
    {
        var settings = await modules.GetAsync(cancellationToken);
        await modules.SaveAsync(settings.With(InstanceModule.Movie, false).With(InstanceModule.Tv, false), cancellationToken);
    }

    private static bool HasInvalid(string? token, string? key) =>
        (token is not null && !TmdbCredentialStore.IsValidSecret(token, TmdbCredentialStore.MaxReadAccessTokenLength))
        || (key is not null && !TmdbCredentialStore.IsValidSecret(key, TmdbCredentialStore.MaxApiKeyLength));

    private static string? Given(IReadOnlyDictionary<string, string?> secrets, string name) =>
        secrets.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private void Reevaluate()
    {
        health.Reset(ProviderKeys.Tmdb);
        cache.RemoveByPrefix("tmdb:");
        flights.Clear();
    }
}
