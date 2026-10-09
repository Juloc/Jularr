using Jularr.Web.Features.Providers;

namespace Jularr.Web.Features.Plex;

public sealed class PlexIdentityProviderSettings(
    PlexIdentitySettingsStore store,
    PlexAuthClient plex) : IProviderSettings
{
    public string Key => "plex";

    public async Task<ProviderView> GetViewAsync(
        CancellationToken cancellationToken)
    {
        var settings = await store.GetAsync(cancellationToken);
        var state = !settings.Enabled
            ? ProviderConnectionState.Disabled
            : settings.ClientIdentifier.Length == 0
                ? ProviderConnectionState.NotConfigured
                : ProviderConnectionState.Unknown;

        return new ProviderView(
            Key,
            "Plex",
            "admin.providers.summary.plex",
            ProviderFamily.Identity,
            settings.Enabled,
            state,
            null,
            [
                "admin.providers.capability.plexLogin",
                "admin.providers.capability.plexLink"
            ],
            [],
            store.ExternallyManaged,
            false,
            false,
            store.ExternallyManaged
                ? "admin.providers.fact.sourceEnvironment"
                : "admin.providers.fact.sourceStored",
            null,
            null,
            null)
        {
            Options =
            [
                new ProviderOptionField(
                    "loginEnabled",
                    "admin.providers.plex.login",
                    "admin.providers.plex.loginHint",
                    settings.LoginEnabled),
                new ProviderOptionField(
                    "linkEnabled",
                    "admin.providers.plex.link",
                    "admin.providers.plex.linkHint",
                    settings.LinkEnabled),
                new ProviderOptionField(
                    "autoProvisionEnabled",
                    "admin.providers.plex.provision",
                    "admin.providers.plex.provisionHint",
                    settings.AutoProvisionEnabled),
                new ProviderOptionField(
                    "requireApproval",
                    "admin.providers.plex.approval",
                    "admin.providers.plex.approvalHint",
                    settings.RequireApproval),
                new ProviderOptionField(
                    "mediaConnectionEnabled",
                    "admin.providers.plex.media",
                    "admin.providers.plex.mediaHint",
                    settings.MediaConnectionEnabled)
            ]
        };
    }

    public async Task<ProviderFeedback> SaveAsync(
        bool enabled,
        IReadOnlyDictionary<string, string?> fields,
        CancellationToken cancellationToken)
    {
        if (store.ExternallyManaged)
        {
            return ProviderFeedback.InvalidInput;
        }

        await store.SaveAsync(
            enabled,
            Selected(fields, "loginEnabled"),
            Selected(fields, "linkEnabled"),
            Selected(fields, "autoProvisionEnabled"),
            Selected(fields, "requireApproval"),
            Selected(fields, "mediaConnectionEnabled"),
            cancellationToken);

        return ProviderFeedback.Saved;
    }

    public async Task<ProviderFeedback> TestAsync(
        IReadOnlyDictionary<string, string?> fields,
        CancellationToken cancellationToken)
    {
        var settings = await store.GetAsync(cancellationToken);
        if (!settings.Enabled || settings.ClientIdentifier.Length == 0)
        {
            return ProviderFeedback.TestNotConfigured;
        }

        try
        {
            await plex.CreatePinAsync(
                settings.ClientIdentifier,
                cancellationToken);
            return ProviderFeedback.TestSucceeded;
        }
        catch (HttpRequestException)
        {
            return ProviderFeedback.TestUnreachable;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderFeedback.TestUnreachable;
        }
    }

    public async Task RemoveSavedValuesAsync(
        CancellationToken cancellationToken)
    {
        var settings = await store.GetAsync(cancellationToken);
        if (!store.ExternallyManaged)
        {
            await store.SaveAsync(
                false,
                false,
                false,
                false,
                settings.RequireApproval,
                false,
                cancellationToken);
        }
    }

    public Task DisableDependentFeaturesAsync(
        CancellationToken cancellationToken) =>
        RemoveSavedValuesAsync(cancellationToken);

    private static bool Selected(
        IReadOnlyDictionary<string, string?> fields,
        string key) =>
        fields.TryGetValue(key, out var value) &&
        value?.Split(',').Any(x =>
            string.Equals(x.Trim(), "true", StringComparison.OrdinalIgnoreCase)) == true;
}
