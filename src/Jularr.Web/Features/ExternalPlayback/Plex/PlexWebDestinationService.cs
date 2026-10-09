using System.Security.Claims;

namespace Jularr.Web.Features.ExternalPlayback.Plex;

/// <summary>
/// Resolves a browser-only Plex detail destination *after* checking current
/// profile access to the exact item. This never exposes credentials and never
/// starts Jularr playback or changes Request/progress state.
/// </summary>
public sealed class PlexWebDestinationService(PlexItemAccessService access)
{
    public async Task<Uri?> ResolveAsync(
        ClaimsPrincipal caller,
        string machineIdentifier,
        string ratingKey,
        long workId,
        string clientIdentifier,
        CancellationToken cancellationToken = default)
    {
        if (!await access.IsAccessibleMatchAsync(
            caller, machineIdentifier, ratingKey, workId,
            clientIdentifier, cancellationToken))
        {
            return null;
        }

        return BuildWebDetailUri(machineIdentifier, ratingKey);
    }

    /// <summary>
    /// Hosted Plex Web details URL; client playback is not automatically
    /// started. The URL format requires a real-device smoke test before
    /// enabling a production external-playback button.
    /// </summary>
    public static Uri BuildWebDetailUri(
        string machineIdentifier,
        string ratingKey)
    {
        if (string.IsNullOrWhiteSpace(machineIdentifier) ||
            machineIdentifier.Length is < 8 or > 160 ||
            !machineIdentifier.All(c =>
                char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new ArgumentException(
                "A validated Plex machine identifier is required.",
                nameof(machineIdentifier));
        }

        if (string.IsNullOrWhiteSpace(ratingKey) ||
            ratingKey.Length > 18 ||
            !ratingKey.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                "A numeric Plex metadata key is required.",
                nameof(ratingKey));
        }

        return new Uri(
            "https://app.plex.tv/desktop/#!/server/" +
            machineIdentifier +
            "/details?key=%2Flibrary%2Fmetadata%2F" +
            ratingKey,
            UriKind.Absolute);
    }
}
