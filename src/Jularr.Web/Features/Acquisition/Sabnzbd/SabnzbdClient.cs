using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Jularr.Web.Features.Acquisition.Sabnzbd;

public interface ISabnzbdClient
{
    Task<SabnzbdConnectionTestResult> TestAsync(
        SabnzbdConnection connection,
        CancellationToken cancellationToken);

    Task<SabnzbdGrabResult> GrabAsync(
        SabnzbdConnection connection,
        SabnzbdGrabRequest grab,
        CancellationToken cancellationToken);

    Task<SabnzbdGrabResult> AddFileAsync(
        SabnzbdConnection connection,
        Stream nzb,
        string fileName,
        string? category,
        CancellationToken cancellationToken);

    Task<SabnzbdQueueSnapshot> GetQueueAsync(
        SabnzbdConnection connection,
        CancellationToken cancellationToken);

    Task<SabnzbdHistorySnapshot> GetHistoryAsync(
        SabnzbdConnection connection,
        IReadOnlyCollection<string>? nzoIds,
        CancellationToken cancellationToken);

    Task<SabnzbdActionResult> CancelAsync(
        SabnzbdConnection connection,
        string nzoId,
        bool deleteFiles,
        CancellationToken cancellationToken);

    Task<SabnzbdActionResult> DeleteHistoryAsync(
        SabnzbdConnection connection,
        string nzoId,
        bool deleteFiles,
        CancellationToken cancellationToken);

    Task<SabnzbdActionResult> RetryAsync(
        SabnzbdConnection connection,
        string nzoId,
        CancellationToken cancellationToken);

    Task<SabnzbdActionResult> PauseAsync(
        SabnzbdConnection connection,
        string nzoId,
        CancellationToken cancellationToken);

    Task<SabnzbdActionResult> ResumeAsync(
        SabnzbdConnection connection,
        string nzoId,
        CancellationToken cancellationToken);
}

/// <summary>
/// The one SABnzbd API client. Every request is a POST so the API key stays
/// in the request body and never appears in URLs or access logs.
/// </summary>
public sealed class SabnzbdClient(HttpClient httpClient) : ISabnzbdClient
{
    /// <summary>External provider id used on canonical Operations.</summary>
    public const string ProviderId = "sabnzbd";

    private const int PageSize = 200;

    public async Task<SabnzbdConnectionTestResult> TestAsync(
        SabnzbdConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            using var versionResponse = await SendAsync(
                connection,
                [Pair("mode", "version")],
                cancellationToken);

            if (!versionResponse.IsSuccessStatusCode)
            {
                return new SabnzbdConnectionTestResult(
                    false,
                    Error: DescribeStatus(versionResponse.StatusCode));
            }

            var versionBody = await versionResponse.Content.ReadAsStringAsync(cancellationToken);
            string? version;
            using (var document = JsonDocument.Parse(versionBody))
            {
                version = ReadString(document.RootElement, "version");
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                return new SabnzbdConnectionTestResult(
                    false,
                    Error: "SABnzbd returned no version.");
            }

            // The version endpoint does not check the API key. Reading the
            // queue proves the key is valid and allows live monitoring.
            using var queueResponse = await SendAsync(
                connection,
                [
                    Pair("mode", "queue"),
                    Pair("start", "0"),
                    Pair("limit", "1")
                ],
                cancellationToken);
            var queueBody = await queueResponse.Content.ReadAsStringAsync(cancellationToken);
            var queueError = queueResponse.IsSuccessStatusCode
                ? ReadApiError(queueBody)
                : DescribeStatus(queueResponse.StatusCode);

            return queueError is null
                ? new SabnzbdConnectionTestResult(true, version, CanMonitor: true)
                : new SabnzbdConnectionTestResult(
                    false,
                    version,
                    $"SABnzbd {version} is reachable but rejected the API key for queue access ({queueError}). "
                    + "Use the full API key, not the NZB key, so Jularr can track progress.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or JsonException
                or UriFormatException
                or TaskCanceledException)
        {
            return new SabnzbdConnectionTestResult(
                false,
                Error: "SABnzbd could not be reached or returned an invalid response.");
        }
    }

    public async Task<SabnzbdGrabResult> GrabAsync(
        SabnzbdConnection connection,
        SabnzbdGrabRequest grab,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grab);

        if (!grab.NzbUrl.IsAbsoluteUri
            || (grab.NzbUrl.Scheme != Uri.UriSchemeHttp
                && grab.NzbUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "NZB URL must be an absolute HTTP(S) URL.",
                nameof(grab));
        }

        var parameters = new List<KeyValuePair<string, string?>>
        {
            Pair("mode", "addurl"),
            Pair("name", grab.NzbUrl.ToString()),
            Pair("cat", CleanOrNull(grab.Category)),
            Pair("nzbname", CleanOrNull(grab.NzbName))
        };

        if (grab.Priority is int priority)
        {
            parameters.Add(Pair(
                "priority",
                priority.ToString(CultureInfo.InvariantCulture)));
        }

        using var response = await SendAsync(
            connection,
            parameters,
            cancellationToken);

        return await ReadGrabResultAsync(response, cancellationToken);
    }

    public async Task<SabnzbdGrabResult> AddFileAsync(
        SabnzbdConnection connection,
        Stream nzb,
        string fileName,
        string? category,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nzb);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("addfile"), "mode");
        content.Add(new StringContent(RequireApiKey(connection)), "apikey");
        content.Add(new StringContent("json"), "output");
        if (CleanOrNull(category) is { } cat)
        {
            content.Add(new StringContent(cat), "cat");
        }

        var file = new StreamContent(nzb);
        content.Add(file, "name", Path.GetFileName(fileName));

        using var response = await httpClient.PostAsync(
            ApiUri(connection),
            content,
            cancellationToken);

        return await ReadGrabResultAsync(response, cancellationToken);
    }

    public async Task<SabnzbdQueueSnapshot> GetQueueAsync(
        SabnzbdConnection connection,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            connection,
            [
                Pair("mode", "queue"),
                Pair("start", "0"),
                Pair("limit", PageSize.ToString(CultureInfo.InvariantCulture))
            ],
            cancellationToken);

        var body = await RequireBodyAsync(response, "queue", cancellationToken);
        return ParseQueueResponse(body);
    }

    public async Task<SabnzbdHistorySnapshot> GetHistoryAsync(
        SabnzbdConnection connection,
        IReadOnlyCollection<string>? nzoIds,
        CancellationToken cancellationToken)
    {
        var parameters = new List<KeyValuePair<string, string?>>
        {
            Pair("mode", "history"),
            Pair("start", "0"),
            Pair("limit", PageSize.ToString(CultureInfo.InvariantCulture))
        };

        if (nzoIds is { Count: > 0 })
        {
            parameters.Add(
                Pair(
                    "nzo_ids",
                    string.Join(
                        ',',
                        nzoIds
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .Select(ValidateNzoId))));
        }

        using var response = await SendAsync(
            connection,
            parameters,
            cancellationToken);

        var body = await RequireBodyAsync(response, "history", cancellationToken);
        return ParseHistoryResponse(body);
    }

    public Task<SabnzbdActionResult> CancelAsync(
        SabnzbdConnection connection,
        string nzoId,
        bool deleteFiles,
        CancellationToken cancellationToken) =>
        ExecuteActionAsync(
            connection,
            [
                Pair("mode", "queue"),
                Pair("name", "delete"),
                Pair("value", ValidateNzoId(nzoId)),
                Pair("del_files", deleteFiles ? "1" : "0")
            ],
            cancellationToken);

    public Task<SabnzbdActionResult> DeleteHistoryAsync(
        SabnzbdConnection connection,
        string nzoId,
        bool deleteFiles,
        CancellationToken cancellationToken) =>
        ExecuteActionAsync(
            connection,
            [
                Pair("mode", "history"),
                Pair("name", "delete"),
                Pair("value", ValidateNzoId(nzoId)),
                Pair("del_files", deleteFiles ? "1" : "0")
            ],
            cancellationToken);

    public Task<SabnzbdActionResult> RetryAsync(
        SabnzbdConnection connection,
        string nzoId,
        CancellationToken cancellationToken) =>
        ExecuteActionAsync(
            connection,
            [
                Pair("mode", "retry"),
                Pair("value", ValidateNzoId(nzoId))
            ],
            cancellationToken,
            readNewNzoId: true);

    public Task<SabnzbdActionResult> PauseAsync(
        SabnzbdConnection connection,
        string nzoId,
        CancellationToken cancellationToken) =>
        ExecuteActionAsync(
            connection,
            [
                Pair("mode", "queue"),
                Pair("name", "pause"),
                Pair("value", ValidateNzoId(nzoId))
            ],
            cancellationToken);

    public Task<SabnzbdActionResult> ResumeAsync(
        SabnzbdConnection connection,
        string nzoId,
        CancellationToken cancellationToken) =>
        ExecuteActionAsync(
            connection,
            [
                Pair("mode", "queue"),
                Pair("name", "resume"),
                Pair("value", ValidateNzoId(nzoId))
            ],
            cancellationToken);

    public static SabnzbdQueueSnapshot ParseQueueResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("queue", out var queue)
            || queue.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                ReadApiError(document.RootElement) ?? "SABnzbd queue payload is missing.");
        }

        var jobs = new List<SabnzbdQueueJob>();
        if (queue.TryGetProperty("slots", out var slots)
            && slots.ValueKind == JsonValueKind.Array)
        {
            foreach (var slot in slots.EnumerateArray())
            {
                if (slot.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = ReadString(slot, "nzo_id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var percentage = ReadDouble(slot, "percentage");
                jobs.Add(
                    new SabnzbdQueueJob(
                        id,
                        ReadString(slot, "filename")
                            ?? ReadString(slot, "name")
                            ?? id,
                        ReadString(slot, "status"),
                        ReadString(slot, "cat")
                            ?? ReadString(slot, "category"),
                        percentage is null ? null : Math.Clamp(percentage.Value, 0, 100),
                        ParseTimeLeft(ReadString(slot, "timeleft")),
                        ReadLong(slot, "bytes")
                            ?? MegabytesToBytes(ReadDouble(slot, "mb")),
                        ReadLong(slot, "bytesleft")
                            ?? MegabytesToBytes(ReadDouble(slot, "mbleft"))));
            }
        }

        var kbPerSecond = ReadDouble(queue, "kbpersec");
        return new SabnzbdQueueSnapshot(
            ReadBool(queue, "paused") ?? false,
            kbPerSecond is >= 0 ? kbPerSecond.Value * 1024d : null,
            ParseTimeLeft(ReadString(queue, "timeleft")),
            jobs);
    }

    public static SabnzbdHistorySnapshot ParseHistoryResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("history", out var history)
            || history.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                ReadApiError(document.RootElement) ?? "SABnzbd history payload is missing.");
        }

        var jobs = new List<SabnzbdHistoryJob>();
        if (history.TryGetProperty("slots", out var slots)
            && slots.ValueKind == JsonValueKind.Array)
        {
            foreach (var slot in slots.EnumerateArray())
            {
                if (slot.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = ReadString(slot, "nzo_id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var status = ReadString(slot, "status");
                var failureMessage =
                    ReadString(slot, "fail_message")
                    ?? ReadString(slot, "error");

                jobs.Add(
                    new SabnzbdHistoryJob(
                        id,
                        ReadString(slot, "name")
                            ?? ReadString(slot, "nzb_name")
                            ?? ReadString(slot, "filename")
                            ?? id,
                        status,
                        ReadString(slot, "category")
                            ?? ReadString(slot, "cat"),
                        ReadString(slot, "storage"),
                        failureMessage,
                        ClassifyFailure(status, failureMessage),
                        ReadUnixDateTimeOffset(slot, "completed")
                            ?? ReadDateTimeOffset(slot, "completed_at"),
                        ReadLong(slot, "bytes")));
            }
        }

        return new SabnzbdHistorySnapshot(jobs);
    }

    /// <summary>
    /// Classifies a SABnzbd history outcome. A <c>Failed</c> status, or a
    /// failure message on a job that did not complete, counts as a failure;
    /// post-processing states such as Verifying, Repairing or Extracting
    /// without a failure message are still in progress.
    /// </summary>
    public static SabnzbdFailureKind ClassifyFailure(
        string? status,
        string? failureMessage)
    {
        var normalizedStatus = status?.Trim();
        if (string.Equals(normalizedStatus, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            return SabnzbdFailureKind.None;
        }

        var failed = string.Equals(normalizedStatus, "Failed", StringComparison.OrdinalIgnoreCase);
        if (!failed && string.IsNullOrWhiteSpace(failureMessage))
        {
            return SabnzbdFailureKind.None;
        }

        var text = (failureMessage ?? "").ToLowerInvariant();

        if (ContainsAny(text, "password", "encrypted"))
        {
            return SabnzbdFailureKind.Password;
        }

        if (ContainsAny(text, "disk full", "out of disk", "no space", "not enough space", "insufficient space", "free space", "permission denied", "access denied", "read-only", "read only", "cannot create", "unable to create", "can't create", "unable to write", "cannot write", "i/o error", "no such file or directory"))
        {
            return SabnzbdFailureKind.Storage;
        }

        if (ContainsAny(text, "unpack", "extract", "unrar", "rar ", "7zip", "7-zip"))
        {
            return SabnzbdFailureKind.Unpack;
        }

        if (ContainsAny(text, "verif", "repair", "par2", "corrupt", "crc"))
        {
            return SabnzbdFailureKind.Verification;
        }

        if (ContainsAny(text, "script"))
        {
            return SabnzbdFailureKind.Script;
        }

        if (ContainsAny(text, "article", "incomplete", "missing", "not enough", "download failed", "aborted"))
        {
            return SabnzbdFailureKind.Download;
        }

        return SabnzbdFailureKind.Unknown;
    }

    public static TimeSpan? ParseTimeLeft(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Trim().Split(':');
        var numbers = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(
                    parts[index],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out numbers[index]))
            {
                return null;
            }
        }

        return numbers.Length switch
        {
            3 => new TimeSpan(numbers[0], numbers[1], numbers[2]),
            4 => new TimeSpan(numbers[0], numbers[1], numbers[2], numbers[3]),
            _ => null
        };
    }

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.Ordinal));

    private async Task<SabnzbdActionResult> ExecuteActionAsync(
        SabnzbdConnection connection,
        IReadOnlyList<KeyValuePair<string, string?>> parameters,
        CancellationToken cancellationToken,
        bool readNewNzoId = false)
    {
        using var response = await SendAsync(
            connection,
            parameters,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new SabnzbdActionResult(
                false,
                Error: DescribeStatus(response.StatusCode));
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var success = ReadBool(root, "status") ?? false;
            var newNzoId = readNewNzoId
                ? ReadString(root, "nzo_id")
                    ?? ReadStringArray(root, "nzo_ids").FirstOrDefault()
                : null;

            return new SabnzbdActionResult(
                success,
                newNzoId,
                success
                    ? null
                    : ReadString(root, "error") ?? "SABnzbd action failed.");
        }
        catch (JsonException exception)
        {
            throw new SabnzbdException(
                "SABnzbd returned invalid action JSON.",
                exception);
        }
    }

    private static async Task<SabnzbdGrabResult> ReadGrabResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new SabnzbdGrabResult(
                false,
                [],
                DescribeStatus(response.StatusCode));
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var success = ReadBool(root, "status") ?? false;
            var ids = ReadStringArray(root, "nzo_ids");

            return new SabnzbdGrabResult(
                success,
                ids,
                success
                    ? null
                    : ReadString(root, "error") ?? "SABnzbd rejected the request.");
        }
        catch (JsonException exception)
        {
            throw new SabnzbdException(
                "SABnzbd returned invalid add JSON.",
                exception);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        SabnzbdConnection connection,
        IReadOnlyList<KeyValuePair<string, string?>> parameters,
        CancellationToken cancellationToken)
    {
        var fields = parameters
            .Where(pair => pair.Value is not null)
            .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value!))
            .Append(new KeyValuePair<string, string>("output", "json"))
            .Append(new KeyValuePair<string, string>("apikey", RequireApiKey(connection)));

        using var content = new FormUrlEncodedContent(fields);
        return await httpClient.PostAsync(
            ApiUri(connection),
            content,
            cancellationToken);
    }

    private static Uri ApiUri(SabnzbdConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var baseUrl = NormalizeBaseUrl(connection.Settings.BaseUrl)
            ?? throw new ArgumentException(
                "SABnzbd URL is required.",
                nameof(connection));

        return new Uri($"{baseUrl}/api", UriKind.Absolute);
    }

    public static string? NormalizeBaseUrl(string? baseUrl)
    {
        var clean = baseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            return null;
        }

        if (!Uri.TryCreate(clean, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "SABnzbd URL must be an absolute HTTP(S) URL without credentials, query or fragment.",
                nameof(baseUrl));
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static string RequireApiKey(SabnzbdConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(connection.ApiKey))
        {
            throw new ArgumentException(
                "SABnzbd API key is required.",
                nameof(connection));
        }

        return connection.ApiKey.Trim();
    }

    private static async Task<string> RequireBodyAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SabnzbdException(
                $"SABnzbd {operation} failed: {DescribeStatus(response.StatusCode)}");
        }

        if (ReadApiError(body) is { } error)
        {
            throw new SabnzbdException($"SABnzbd {operation} failed: {error}");
        }

        return body;
    }

    private static string? ReadApiError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return ReadApiError(document.RootElement);
        }
        catch (JsonException)
        {
            return "SABnzbd returned an invalid response.";
        }
    }

    private static string? ReadApiError(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && ReadBool(root, "status") == false
            ? ReadString(root, "error") ?? "SABnzbd rejected the request."
            : null;

    private static string ValidateNzoId(string nzoId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nzoId);
        var normalized = nzoId.Trim();
        if (normalized.Length > 200
            || normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                  || character is '_' or '-' or '.')))
        {
            throw new ArgumentException(
                "Invalid SABnzbd job ID.",
                nameof(nzoId));
        }

        return normalized;
    }

    private static string? CleanOrNull(string? value)
    {
        var clean = value?.Trim();
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }

    private static KeyValuePair<string, string?> Pair(
        string key,
        string? value) =>
        new(key, value);

    private static string DescribeStatus(HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "SABnzbd rejected the API key.",
            HttpStatusCode.NotFound =>
                "SABnzbd API endpoint was not found. Check the URL.",
            _ =>
                $"SABnzbd returned HTTP {(int)statusCode}."
        };

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return value.ValueKind == JsonValueKind.Number
            ? value.GetRawText()
            : null;
    }

    private static bool? ReadBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) =>
                number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) =>
                parsed,
            JsonValueKind.String when value.GetString() == "1" => true,
            JsonValueKind.String when value.GetString() == "0" => false,
            _ => null
        };
    }

    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        var raw = ReadString(element, propertyName);
        return double.TryParse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            && double.IsFinite(value)
            ? value
            : null;
    }

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        var raw = ReadString(element, propertyName);
        return long.TryParse(
            raw,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static long? MegabytesToBytes(double? megabytes)
    {
        if (megabytes is null || megabytes.Value < 0)
        {
            return null;
        }

        var bytes = megabytes.Value * 1024d * 1024d;
        return bytes >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Round(bytes);
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement element,
        string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }

    private static DateTimeOffset? ReadUnixDateTimeOffset(
        JsonElement element,
        string propertyName)
    {
        var raw = ReadString(element, propertyName);
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            || seconds <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadDateTimeOffset(
        JsonElement element,
        string propertyName)
    {
        var raw = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : null;
    }
}
