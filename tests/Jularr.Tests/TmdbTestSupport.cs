using Jularr.Web.Features.Discovery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace Jularr.Tests;

/// <summary>The TMDB credential owner of a test: a deployment credential by default, an isolated settings directory when a test saves one.</summary>
internal static class TmdbTestSupport
{
    /// <param name="apiKey">The deployment API key; null for none.</param>
    /// <param name="readAccessToken">The deployment Read Access Token; null for none.</param>
    /// <param name="directory">Where the stored settings live; defaults to a directory that does not exist, so nothing is stored.</param>
    public static TmdbCredentialStore Credentials(string? apiKey = "test-key", string? readAccessToken = null, string? directory = null, IDataProtectionProvider? protection = null, TimeProvider? clock = null)
    {
        var configuration = new ConfigurationManager();
        if (apiKey is not null)
        {
            configuration[TmdbCredentialStore.ApiKeyKey] = apiKey;
        }

        if (readAccessToken is not null)
        {
            configuration[TmdbCredentialStore.ReadAccessTokenKey] = readAccessToken;
        }

        directory ??= Path.Combine(Path.GetTempPath(), "jularr-tmdb-none-" + Guid.NewGuid().ToString("N"));
        return new TmdbCredentialStore(configuration, protection ?? new EphemeralDataProtectionProvider(), clock ?? TimeProvider.System, directory);
    }
}
