using System.Globalization;
using System.Text.Json;
using Jularr.Integrations.Data.Sabnzbd;

namespace Jularr.Integrations.Logic.Sabnzbd
{
    // Every request is a form POST, so the API key never appears in URLs or access logs.
    internal sealed class SabnzbdClient(HttpClient httpClient)
    {
        private const string PageSize = "200";
        private const int IdsPerRequest = 50;

        public async Task<SabnzbdConnectionTestResult> TestAsync(SabnzbdConnection connection, CancellationToken ct)
        {
            try
            {
                // The version endpoint does not check the key.
                using var versionResponse = await SendAsync(connection, [new("mode", "version")], ct);
                string? version = null;
                if (versionResponse.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync(ct));
                    version = ReadString(document.RootElement, "version");
                }

                if (version is null)
                {
                    return new SabnzbdConnectionTestResult(false, null);
                }

                // Reading the queue proves the full API key.
                using var queueResponse = await SendAsync(connection, [new("mode", "queue"), new("start", "0"), new("limit", "1")], ct);
                var accepted = queueResponse.IsSuccessStatusCode && ReadApiError(await queueResponse.Content.ReadAsStringAsync(ct)) is null;
                return new SabnzbdConnectionTestResult(accepted, version);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                return new SabnzbdConnectionTestResult(false, null);
            }
        }

        // Discover the actual SAB folder; categories can override the global Complete folder.
        public async Task<string> GetCompletedFolderAsync(SabnzbdConnection connection, string? category, CancellationToken ct)
        {
            using var status = await SendAsync(connection, [new("mode", "status"), new("skip_dashboard", "1")], ct);
            var statusJson = await RequireBodyAsync(status, ct);
            // Even the default '*' category can have its own configured target folder.
            using var config = await SendAsync(connection, [new("mode", "get_config"), new("section", "categories")], ct);
            return ParseCompletedFolder(statusJson, await RequireBodyAsync(config, ct), string.IsNullOrWhiteSpace(category) ? "*" : category);
        }

        internal static string ParseCompletedFolder(string statusJson, string? configJson = null, string? category = null)
        {
            using var status = JsonDocument.Parse(statusJson);
            if (status.RootElement.ValueKind != JsonValueKind.Object || !status.RootElement.TryGetProperty("status", out var details)
                || details.ValueKind != JsonValueKind.Object)
                throw new SabnzbdException("SABnzbd did not provide a completed download folder.");
            var root = AbsoluteFolder(ReadString(details, "completedir"));
            if (configJson is null)
            {
                if (category is null) return root;
                throw new SabnzbdException("SABnzbd category configuration is missing.");
            }
            category = string.IsNullOrWhiteSpace(category) ? "*" : category;
            using var config = JsonDocument.Parse(configJson);
            if (config.RootElement.ValueKind != JsonValueKind.Object || !config.RootElement.TryGetProperty("config", out var settings)
                || settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty("categories", out var categories)
                || categories.ValueKind != JsonValueKind.Array)
                throw new SabnzbdException("SABnzbd category folders could not be read.");

            var matches = categories.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object
                && string.Equals(ReadString(item, "name"), category, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new SabnzbdException("SABnzbd category folder is missing or ambiguous.");

            if (!matches[0].TryGetProperty("dir", out var categoryDir) || categoryDir.ValueKind != JsonValueKind.String)
                throw new SabnzbdException("SABnzbd category directory could not be read.");
            var folder = categoryDir.GetString() ?? "";
            // The trailing '*' disables per-job folders; never assume which media belongs to Jularr.
            if (folder.EndsWith('*')) throw new SabnzbdException("SABnzbd category must create individual job folders.");
            folder = folder.Replace('\\', '/').Trim('/');
            if (folder.Length == 0) return root;
            if (folder.Split('/').Any(part => part is "." or "..") || folder.Any(char.IsControl))
                throw new SabnzbdException("SABnzbd category folder is unsafe.");

            var raw = ReadString(matches[0], "dir")!.Replace('\\', '/');
            return IsAbsoluteFolder(raw) ? AbsoluteFolder(raw) : AbsoluteFolder(root.TrimEnd('/') + "/" + folder);
        }

        private static bool IsAbsoluteFolder(string value) => value.StartsWith('/') || value.Length >= 3
            && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '/' or '\\';

        private static string AbsoluteFolder(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new SabnzbdException("SABnzbd completed folder is empty.");
            var path = value.Replace('\\', '/').TrimEnd('/');
            if (path.Length == 0 && value.StartsWith('/')) path = "/";
            if (!IsAbsoluteFolder(path) || path.Any(char.IsControl) || path.Split('/').Any(part => part is "." or ".."))
                throw new SabnzbdException("SABnzbd completed folder is not a safe absolute path.");
            return path;
        }

        public async Task<SabnzbdGrabResult> GrabAsync(SabnzbdConnection connection, SabnzbdGrabRequest grab, CancellationToken ct)
        {
            if (!grab.NzbUrl.IsAbsoluteUri || grab.NzbUrl.Scheme is not ("http" or "https"))
            {
                throw new ArgumentException("The NZB address must be an absolute HTTP(S) address.", nameof(grab));
            }

            var parameters = new List<KeyValuePair<string, string?>> { new("mode", "addurl"), new("name", grab.NzbUrl.ToString()), new("cat", Clean(grab.Category)), new("nzbname", Clean(grab.NzbName)) };
            using var response = await SendAsync(connection, parameters, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new SabnzbdGrabResult(false, []);
            }

            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                return new SabnzbdGrabResult(ReadBool(document.RootElement, "status") ?? false, ReadStringArray(document.RootElement, "nzo_ids"));
            }
            catch (JsonException exception)
            {
                throw new SabnzbdException("SABnzbd returned an invalid add response.", exception);
            }
        }

        public async Task<SabnzbdQueueSnapshot> GetQueueAsync(SabnzbdConnection connection, IReadOnlyCollection<string> nzoIds, CancellationToken ct)
        {
            // Tracked jobs are asked for directly, so long queues never hide them.
            var jobs = new List<SabnzbdQueueJob>();
            foreach (var batch in nzoIds.Select(ValidateNzoId).Chunk(IdsPerRequest))
            {
                using var response = await SendAsync(connection, [new("mode", "queue"), new("start", "0"), new("limit", PageSize), new("nzo_ids", string.Join(',', batch))], ct);
                jobs.AddRange(ParseQueueResponse(await RequireBodyAsync(response, ct)).Jobs);
            }

            return new SabnzbdQueueSnapshot(jobs);
        }

        public async Task<SabnzbdHistorySnapshot> GetHistoryAsync(SabnzbdConnection connection, IReadOnlyCollection<string> nzoIds, CancellationToken ct)
        {
            var jobs = new List<SabnzbdHistoryJob>();
            foreach (var batch in nzoIds.Select(ValidateNzoId).Chunk(IdsPerRequest))
            {
                using var response = await SendAsync(connection, [new("mode", "history"), new("start", "0"), new("limit", PageSize), new("nzo_ids", string.Join(',', batch))], ct);
                jobs.AddRange(ParseHistoryResponse(await RequireBodyAsync(response, ct)).Jobs);
            }

            return new SabnzbdHistorySnapshot(jobs);
        }

        // The job leaves the queue and the history; its files go with it when asked.
        public async Task RemoveJobAsync(SabnzbdConnection connection, string nzoId, bool deleteFiles, CancellationToken ct)
        {
            ValidateNzoId(nzoId);
            foreach (var mode in new[] { "queue", "history" })
            {
                using var response = await SendAsync(connection, [new("mode", mode), new("name", "delete"), new("value", nzoId), new("del_files", deleteFiles ? "1" : "0")], ct);
                RequireStatus(await RequireBodyAsync(response, ct));
            }
        }

        // Remove a terminal history job only: never cancel an active queue item during automatic cleanup.
        public async Task RemoveHistoryJobAsync(SabnzbdConnection connection, string nzoId, CancellationToken ct)
        {
            var id = ValidateNzoId(nzoId);
            using var response = await SendAsync(connection,
                [new("mode", "history"), new("name", "delete"), new("value", id), new("del_files", "1")], ct);
            RequireStatus(await RequireBodyAsync(response, ct));
        }

        public async Task PauseJobAsync(SabnzbdConnection connection, string nzoId, bool isPaused, CancellationToken ct)
        {
            ValidateNzoId(nzoId);
            using var response = await SendAsync(connection, [new("mode", "queue"), new("name", isPaused ? "pause" : "resume"), new("value", nzoId)], ct);
            RequireStatus(await RequireBodyAsync(response, ct));
        }

        private static void RequireStatus(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (ReadBool(document.RootElement, "status") is not true)
                {
                    throw new SabnzbdException("SABnzbd did not accept the command.");
                }
            }
            catch (JsonException exception)
            {
                throw new SabnzbdException("SABnzbd returned an invalid answer.", exception);
            }
        }

        // Bounded full queue/history reads for the admin console. Tracked-job reads remain unchanged.
        public async Task<SabnzbdConsoleSnapshot> ReadConsoleAsync(SabnzbdConnection connection, CancellationToken ct)
        {
            using var queueResponse = await SendAsync(connection, [new("mode", "queue"), new("start", "0"), new("limit", PageSize)], ct);
            var queueJson = await RequireBodyAsync(queueResponse, ct);
            using var queueDocument = JsonDocument.Parse(queueJson);
            var queue = queueDocument.RootElement.GetProperty("queue");
            var speedKb = ReadDouble(queue, "kbpersec");
            var diskMb = ReadDouble(queue, "diskspace1");

            using var historyResponse = await SendAsync(connection, [new("mode", "history"), new("start", "0"), new("limit", PageSize)], ct);
            var historyJson = await RequireBodyAsync(historyResponse, ct);

            using var statisticsResponse = await SendAsync(connection, [new("mode", "server_stats")], ct);
            using var statisticsDocument = JsonDocument.Parse(await RequireBodyAsync(statisticsResponse, ct));
            var usage = statisticsDocument.RootElement;

            using var statusResponse = await SendAsync(connection, [new("mode", "status")], ct);
            using var statusDocument = JsonDocument.Parse(await RequireBodyAsync(statusResponse, ct));
            var status = statusDocument.RootElement.GetProperty("status");

            return new SabnzbdConsoleSnapshot(ParseQueueResponse(queueJson).Jobs, ParseHistoryResponse(historyJson).Jobs,
                speedKb is >= 0 and < 1e9 ? (long?)Math.Round(speedKb.Value * 1024) : null,
                diskMb is >= 0 and < 1e6 ? (long?)Math.Round(diskMb.Value * 1024 * 1024) : null,
                ReadBool(queue, "paused") is true,
                ReadLong(usage, "day"), ReadLong(usage, "week"), ReadLong(usage, "month"), ReadLong(usage, "total"),
                ReadStringArray(status, "folders"), ReadString(status, "completedir"), ReadString(status, "downloaddir"));
        }

        public async Task<IReadOnlyList<string>> ReadCategoriesAsync(SabnzbdConnection connection, CancellationToken ct)
        {
            using var response = await SendAsync(connection, [new("mode", "get_cats")], ct);
            using var document = JsonDocument.Parse(await RequireBodyAsync(response, ct));
            return ReadStringArray(document.RootElement, "categories");
        }

        // Never accept a provider mode from user input; destructive operations have explicit branches.
        public async Task RemoveOrphanAsync(SabnzbdConnection connection, string folderName, CancellationToken ct)
        {
            if (folderName.Length is 0 or > 200 || folderName is "." or ".."
                || folderName.Any(character => char.IsControl(character) || character is '/' or '\\'))
            {
                throw new ArgumentException("Invalid orphan folder name.", nameof(folderName));
            }

            using var response = await SendAsync(connection,
                [new("mode", "status"), new("name", "delete_orphan"), new("value", folderName)], ct);
            using var document = JsonDocument.Parse(await RequireBodyAsync(response, ct));
            if (ReadBool(document.RootElement, "status") is not true)
            {
                throw new SabnzbdException("SABnzbd did not confirm orphan removal.");
            }
        }

        public async Task ControlJobAsync(SabnzbdConnection connection, string nzoId, SabnzbdJobCommand command, CancellationToken ct)
        {
            var id = ValidateNzoId(nzoId);
            KeyValuePair<string, string?>[] fields = command switch
            {
                SabnzbdJobCommand.Pause => [new("mode", "queue"), new("name", "pause"), new("value", id)],
                SabnzbdJobCommand.Resume => [new("mode", "queue"), new("name", "resume"), new("value", id)],
                SabnzbdJobCommand.RemoveFromQueue => [new("mode", "queue"), new("name", "delete"), new("value", id), new("del_files", "1")],
                SabnzbdJobCommand.RemoveFromHistory => [new("mode", "history"), new("name", "delete"), new("value", id), new("archive", "0"), new("del_files", "0")],
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            };

            using var response = await SendAsync(connection, fields, ct);
            using var document = JsonDocument.Parse(await RequireBodyAsync(response, ct));
            if (ReadBool(document.RootElement, "status") is not true)
            {
                throw new SabnzbdException("SABnzbd did not accept the requested action.");
            }
        }

        public async Task<string?> FindJobAsync(SabnzbdConnection connection, string name, CancellationToken ct)
        {
            // Recovers a job whose id was never stored.
            using var queueResponse = await SendAsync(connection, [new("mode", "queue"), new("start", "0"), new("limit", "20"), new("search", name)], ct);
            var queued = ParseQueueResponse(await RequireBodyAsync(queueResponse, ct)).Jobs.FirstOrDefault(job => string.Equals(job.Name, name, StringComparison.OrdinalIgnoreCase));
            if (queued is not null)
            {
                return queued.NzoId;
            }

            using var historyResponse = await SendAsync(connection, [new("mode", "history"), new("start", "0"), new("limit", "20"), new("search", name)], ct);
            return ParseHistoryResponse(await RequireBodyAsync(historyResponse, ct)).Jobs.FirstOrDefault(job => string.Equals(job.Name, name, StringComparison.OrdinalIgnoreCase))?.NzoId;
        }

        internal static SabnzbdQueueSnapshot ParseQueueResponse(string json)
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("queue", out var queue) || queue.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The SABnzbd queue is missing.");
            }

            var jobs = new List<SabnzbdQueueJob>();
            var paused = ReadBool(queue, "paused") is true;
            foreach (var slot in Slots(queue))
            {
                if (ReadString(slot, "nzo_id") is { } id)
                {
                    var percentage = ReadDouble(slot, "percentage");
                    jobs.Add(new SabnzbdQueueJob(id, ReadString(slot, "filename") ?? ReadString(slot, "name") ?? id, percentage is null ? null : Math.Clamp(percentage.Value, 0, 100),
                        Megabytes(ReadDouble(slot, "mb")), Megabytes(ReadDouble(slot, "mbleft")), Duration(ReadString(slot, "timeleft")), paused ? "Paused" : ReadString(slot, "status")));
                }
            }

            return new SabnzbdQueueSnapshot(jobs);
        }

        internal static SabnzbdHistorySnapshot ParseHistoryResponse(string json)
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("history", out var history) || history.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The SABnzbd history is missing.");
            }

            var jobs = new List<SabnzbdHistoryJob>();
            foreach (var slot in Slots(history))
            {
                if (ReadString(slot, "nzo_id") is { } id)
                {
                    var status = ReadString(slot, "status");
                    jobs.Add(new SabnzbdHistoryJob(id, ReadString(slot, "name") ?? id, status, ReadString(slot, "storage"),
                        ClassifyFailure(status, ReadString(slot, "fail_message") ?? ReadString(slot, "error"))));
                }
            }

            return new SabnzbdHistorySnapshot(jobs);
        }

        // Post-processing without a failure message is still in progress.
        internal static SabnzbdFailureKind ClassifyFailure(string? status, string? failureMessage)
        {
            if (string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(failureMessage))
            {
                return SabnzbdFailureKind.None;
            }

            var text = (failureMessage ?? "").ToLowerInvariant();
            if (ContainsAny(text, "password", "encrypted")) return SabnzbdFailureKind.Password;
            if (ContainsAny(text, "disk full", "out of disk", "no space", "not enough space", "insufficient space", "free space", "permission denied", "access denied", "read-only",
                "read only", "cannot create", "unable to create", "can't create", "unable to write", "cannot write", "i/o error", "no such file or directory"))
            {
                return SabnzbdFailureKind.Storage;
            }

            if (ContainsAny(text, "unpack", "extract", "unrar", "rar ", "7zip", "7-zip")) return SabnzbdFailureKind.Unpack;
            if (ContainsAny(text, "verif", "repair", "par2", "corrupt", "crc")) return SabnzbdFailureKind.Verification;
            if (ContainsAny(text, "script")) return SabnzbdFailureKind.Script;
            return ContainsAny(text, "article", "incomplete", "missing", "not enough", "download failed", "aborted") ? SabnzbdFailureKind.Download : SabnzbdFailureKind.Unknown;
        }

        private async Task<HttpResponseMessage> SendAsync(SabnzbdConnection connection, IReadOnlyList<KeyValuePair<string, string?>> parameters, CancellationToken ct)
        {
            var fields = parameters.Where(pair => pair.Value is not null).Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value!))
                .Append(new("output", "json")).Append(new("apikey", connection.ApiKey.Trim()));
            using var content = new FormUrlEncodedContent(fields);
            return await httpClient.PostAsync(new Uri(connection.BaseUrl.Trim().TrimEnd('/') + "/api", UriKind.Absolute), content, ct);
        }

        private static async Task<string> RequireBodyAsync(HttpResponseMessage response, CancellationToken ct)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode || ReadApiError(body) is not null)
            {
                throw new SabnzbdException("SABnzbd rejected the request.");
            }

            return body;
        }

        private static string? ReadApiError(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.ValueKind == JsonValueKind.Object && ReadBool(document.RootElement, "status") == false
                    ? ReadString(document.RootElement, "error") ?? "rejected"
                    : null;
            }
            catch (JsonException)
            {
                return "invalid";
            }
        }

        private static string ValidateNzoId(string nzoId)
        {
            var normalized = nzoId.Trim();
            if (normalized.Length is 0 or > 200 || normalized.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')))
            {
                throw new ArgumentException("The SABnzbd job id is invalid.", nameof(nzoId));
            }

            return normalized;
        }

        private static IEnumerable<JsonElement> Slots(JsonElement container) =>
            container.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array
                ? slots.EnumerateArray().Where(slot => slot.ValueKind == JsonValueKind.Object).ToArray()
                : [];

        private static bool ContainsAny(string text, params string[] needles) => needles.Any(needle => text.Contains(needle, StringComparison.Ordinal));

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string? ReadString(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => Clean(value.GetString()),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
        }

        private static bool? ReadBool(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
                JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
                JsonValueKind.String when value.GetString() is "1" or "0" => value.GetString() == "1",
                _ => null
            };
        }

        private static long? ReadLong(JsonElement element, string name) =>
            long.TryParse(ReadString(element, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;

        private static long? Megabytes(double? value) => value is >= 0 and < 1e9 ? (long)Math.Round(value.Value * 1024 * 1024) : null;

        // "h:mm:ss" or "d:hh:mm:ss"; zero means unknown.
        private static int? Duration(string? value)
        {
            var parts = value?.Split(':');
            if (parts is not { Length: 3 or 4 } || parts.Any(part => !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                return null;
            }

            var numbers = parts.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
            var seconds = numbers.Length == 4 ? (((long)numbers[0] * 24 + numbers[1]) * 60 + numbers[2]) * 60 + numbers[3] : ((long)numbers[0] * 60 + numbers[1]) * 60 + numbers[2];
            return seconds is > 0 and <= int.MaxValue ? (int)seconds : null;
        }

        private static double? ReadDouble(JsonElement element, string name) =>
            double.TryParse(ReadString(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

        private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
                ? array.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? Clean(item.GetString()) : null).OfType<string>().ToArray()
                : [];
    }
}
