using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jularr.Web.Features.ExternalPlayback.Plex;

/// <summary>
/// Discovers owned and shared Plex servers from the authenticated Plex account.
/// Grants are transient; do not serialize or expose server access tokens to a UI.
/// </summary>
public sealed class PlexResourceDiscoveryClient(HttpClient client)
{
    public async Task<IReadOnlyList<PlexServerCandidate>> DiscoverAsync(
        string plexAccountToken,
        string clientIdentifier,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plexAccountToken) ||
            plexAccountToken.Length > 2048 ||
            plexAccountToken.Any(c => c is <= ' ' or > '~'))
        {
            throw new ArgumentException(
                "A valid Plex account token is required.", nameof(plexAccountToken));
        }

        if (string.IsNullOrWhiteSpace(clientIdentifier) ||
            clientIdentifier.Length > 120 ||
            clientIdentifier.Any(c => !char.IsAsciiLetterOrDigit(c) &&
                c is not ('-' or '_' or '.')))
        {
            throw new ArgumentException(
                "A valid Plex client identifier is required.", nameof(clientIdentifier));
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "api/v2/resources?includeHttps=1&includeRelay=1&includeIPv6=1");
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(
            "X-Plex-Product", "Jularr");
        request.Headers.TryAddWithoutValidation(
            "X-Plex-Client-Identifier", clientIdentifier);
        request.Headers.TryAddWithoutValidation(
            "X-Plex-Token", plexAccountToken);

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently
            or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect)
        {
            throw new HttpRequestException(
                "Plex resource discovery redirected a credentialed request.");
        }

        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(
            stream, cancellationToken: cancellationToken);

        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "Plex returned an invalid server resource document.");
        }

        var servers = new List<PlexServerCandidate>();
        foreach (var resource in json.RootElement.EnumerateArray().Take(100))
        {
            var machineId = String(resource, "clientIdentifier");
            var serverToken = String(resource, "accessToken");
            var provides = String(resource, "provides");
            if (!provides.Split(',', StringSplitOptions.TrimEntries)
                    .Contains("server", StringComparer.OrdinalIgnoreCase) ||
                machineId.Length is < 8 or > 160 ||
                !machineId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ||
                serverToken.Length is < 1 or > 2048)
            {
                continue;
            }

            var connections = new List<PlexServerConnection>();
            if (resource.TryGetProperty("connections", out var list) &&
                list.ValueKind == JsonValueKind.Array)
            {
                foreach (var connection in list.EnumerateArray().Take(40))
                {
                    if (!Uri.TryCreate(String(connection, "uri"), UriKind.Absolute, out var uri) ||
                        uri.Scheme != Uri.UriSchemeHttps ||
                        uri.UserInfo.Length > 0 || uri.Query.Length > 0 ||
                        uri.Fragment.Length > 0)
                    {
                        continue;
                    }

                    connections.Add(new PlexServerConnection(
                        uri, Bool(connection, "local"), Bool(connection, "relay")));
                }
            }

            if (connections.Count == 0)
            {
                continue;
            }

            servers.Add(new PlexServerCandidate(
                machineId, String(resource, "name"),
                Bool(resource, "owned"), serverToken,
                connections
                    .OrderBy(x => x.IsRelay)
                    .ThenByDescending(x => x.IsLocal)
                    .ToArray()));
        }

        return servers;
    }

    private static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";

    private static bool Bool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.True ||
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) &&
                number != 0 ||
            value.ValueKind == JsonValueKind.String && value.GetString() is "1" or "true";
    }
}

public sealed class PlexServerCandidate(
    string machineIdentifier,
    string name,
    bool owned,
    string accessToken,
    IReadOnlyList<PlexServerConnection> connections)
{
    public string MachineIdentifier { get; } = machineIdentifier;
    public string Name { get; } = name;
    public bool Owned { get; } = owned;
    public IReadOnlyList<PlexServerConnection> Connections { get; } = connections;

    [JsonIgnore]
    internal string AccessToken { get; } = accessToken;
}

public sealed record PlexServerConnection(Uri Url, bool IsLocal, bool IsRelay);
