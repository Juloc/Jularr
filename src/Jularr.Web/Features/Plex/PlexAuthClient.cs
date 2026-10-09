using System.Net.Http.Headers;
using System.Text.Json;

namespace Jularr.Web.Features.Plex;

public sealed record PlexPin(long Id, string Code);

/// <summary>
/// A server-verified Plex identity with an optional media grant. Tokens stay
/// inside the backend and must never appear in UI, JSON, links or logs.
/// </summary>
public sealed class PlexVerifiedIdentity
{
    internal PlexVerifiedIdentity(string accountId, string accessToken)
    {
        AccountId = accountId;
        AccessToken = accessToken;
    }

    public string AccountId { get; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal string AccessToken { get; }

    public override string ToString() => $"PlexVerifiedIdentity({AccountId})";
}

public sealed class PlexAuthClient(HttpClient httpClient)
{
    public async Task<PlexPin> CreatePinAsync(
        string clientIdentifier,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            "api/v2/pins?strong=true",
            clientIdentifier);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var json = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        var root = json.RootElement;
        var pinId = root.GetProperty("id").GetInt64();
        var code = root.GetProperty("code").GetString();
        if (pinId <= 0 || string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException(
                "Plex did not return a valid authentication PIN.");
        }

        return new PlexPin(pinId, code);
    }

    public async Task<string?> ResolveAuthenticatedAccountIdAsync(
        long pinId,
        string clientIdentifier,
        CancellationToken cancellationToken) =>
        (await ResolveVerifiedIdentityAsync(
            pinId, clientIdentifier, cancellationToken))?.AccountId;

    /// <summary>
    /// Consumes the same Plex PIN verification used for Jularr login.
    /// Only an explicit, separately confirmed Profile Connection workflow
    /// may persist the validated media token.
    /// </summary>
    public async Task<PlexVerifiedIdentity?> ResolveVerifiedIdentityAsync(
        long pinId,
        string clientIdentifier,
        CancellationToken cancellationToken)
    {
        if (pinId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pinId));
        }

        using var pinRequest = CreateRequest(
            HttpMethod.Get,
            $"api/v2/pins/{pinId}",
            clientIdentifier);
        using var pinResponse = await httpClient.SendAsync(
            pinRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        pinResponse.EnsureSuccessStatusCode();

        await using var pinStream = await pinResponse.Content.ReadAsStreamAsync(
            cancellationToken);
        using var pinJson = await JsonDocument.ParseAsync(
            pinStream,
            cancellationToken: cancellationToken);
        var pin = pinJson.RootElement;
        if (!pin.TryGetProperty("authToken", out var tokenElement)
            || tokenElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var token = tokenElement.GetString();
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        using var userRequest = CreateRequest(
            HttpMethod.Get,
            "api/v2/user",
            clientIdentifier);
        userRequest.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        using var userResponse = await httpClient.SendAsync(
            userRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        userResponse.EnsureSuccessStatusCode();

        await using var userStream = await userResponse.Content.ReadAsStreamAsync(
            cancellationToken);
        using var userJson = await JsonDocument.ParseAsync(
            userStream,
            cancellationToken: cancellationToken);
        var user = userJson.RootElement;
        if (!user.TryGetProperty("id", out var id)
            || id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
        {
            throw new InvalidOperationException(
                "Plex did not provide a stable account identifier.");
        }

        var accountId = id.ToString();
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 160)
        {
            throw new InvalidOperationException(
                "Plex returned an invalid account identifier.");
        }

        return new PlexVerifiedIdentity(accountId, token);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string relativePath,
        string clientIdentifier)
    {
        if (string.IsNullOrWhiteSpace(clientIdentifier)
            || clientIdentifier.Length > 120
            || clientIdentifier.Any(c => !char.IsAsciiLetterOrDigit(c)
                && c is not ('-' or '_' or '.')))
        {
            throw new ArgumentException(
                "A stable Plex client identifier must be configured.",
                nameof(clientIdentifier));
        }

        var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(
            "X-Plex-Client-Identifier",
            clientIdentifier);
        request.Headers.TryAddWithoutValidation(
            "X-Plex-Product",
            "Jularr");
        return request;
    }
}
