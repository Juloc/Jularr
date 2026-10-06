namespace Jularr.Web.Features.Acquisition.Sabnzbd;

/// <summary>
/// Supported configuration keys. A configured key overrides the matching
/// stored field; there is no other source of SABnzbd settings.
/// Environment variables use double underscores, for example
/// <c>Sabnzbd__ApiKey</c>.
/// </summary>
public static class SabnzbdConfigurationKeys
{
    public const string BaseUrl = "Sabnzbd:BaseUrl";
    public const string ApiKey = "Sabnzbd:ApiKey";
    public const string BooksCategory = "Sabnzbd:Categories:Books";
    public const string AnimeCategory = "Sabnzbd:Categories:Anime";

}

public sealed record SabnzbdResolvedSettings(
    SabnzbdStoredSettings Stored,
    SabnzbdStoredSettings Effective,
    SabnzbdConnection? Connection,
    bool BaseUrlFromConfiguration,
    bool ApiKeyFromConfiguration,
    bool BooksCategoryFromConfiguration,
    bool AnimeCategoryFromConfiguration,
    string? ConfigurationError = null)
{
    public bool IsConfigured => Connection is not null;
}

/// <summary>
/// Resolves the one effective SABnzbd connection from the canonical store
/// and the canonical configuration keys.
/// </summary>
public sealed class SabnzbdConnectionResolver(
    SabnzbdSettingsStore store,
    IConfiguration configuration)
{
    public async Task<SabnzbdResolvedSettings> ResolveAsync(
        CancellationToken cancellationToken = default)
    {
        var stored = await store.LoadAsync(cancellationToken);

        var baseUrl = Configured(SabnzbdConfigurationKeys.BaseUrl);
        var apiKey = Configured(SabnzbdConfigurationKeys.ApiKey);
        var books = Configured(SabnzbdConfigurationKeys.BooksCategory);
        var anime = Configured(SabnzbdConfigurationKeys.AnimeCategory);

        SabnzbdStoredSettings effective;
        string? error = null;
        try
        {
            effective = SabnzbdSettingsStore.Normalize(
                new SabnzbdStoredSettings(
                    baseUrl ?? stored.BaseUrl,
                    apiKey ?? stored.ApiKey,
                    books ?? stored.BooksCategory,
                    anime ?? stored.AnimeCategory));
        }
        catch (ArgumentException exception)
        {
            // Only configuration keys can be invalid here; stored values are
            // validated when saved. Report it instead of guessing.
            effective = stored;
            error = $"Invalid SABnzbd configuration key value: {exception.Message}";
        }

        var connection = error is not null || effective.BaseUrl is null || effective.ApiKey is null
            ? null
            : new SabnzbdConnection(
                new SabnzbdSettings(
                    effective.BaseUrl,
                    effective.BooksCategory,
                    effective.AnimeCategory),
                effective.ApiKey);

        return new SabnzbdResolvedSettings(
            stored,
            effective,
            connection,
            baseUrl is not null,
            apiKey is not null,
            books is not null,
            anime is not null,
            error);
    }

    public async Task<SabnzbdConnection?> GetConnectionAsync(
        CancellationToken cancellationToken = default) =>
        (await ResolveAsync(cancellationToken)).Connection;

    public async Task<SabnzbdConnection> RequireConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAsync(cancellationToken);
        return resolved.Connection
            ?? throw new InvalidOperationException(
                resolved.ConfigurationError
                ?? "SABnzbd is not configured. Configure it under Settings → SABnzbd.");
    }

    private string? Configured(string key)
    {
        var value = configuration[key]?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
