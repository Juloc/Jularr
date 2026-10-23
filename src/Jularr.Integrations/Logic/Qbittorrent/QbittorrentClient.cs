using System.Globalization;
using System.Text.Json;
using Jularr.Integrations.Data.Qbittorrent;

namespace Jularr.Integrations.Logic.Qbittorrent
{
    // The web interface of the client: an account, a list of torrents and a way to add and remove them.
    // The session lives in a cookie the client sets on the login; it is kept per call, so two clients
    // never share it.
    internal sealed class QbittorrentClient(HttpClient httpClient)
    {
        private const string LoginPath = "/api/v2/auth/login";
        private const string VersionPath = "/api/v2/app/version";
        private const string PreferencesPath = "/api/v2/app/preferences";
        private const string InfoPath = "/api/v2/torrents/info";
        private const string AddPath = "/api/v2/torrents/add";
        private const string DeletePath = "/api/v2/torrents/delete";
        private const string PausePath = "/api/v2/torrents/pause";
        private const string ResumePath = "/api/v2/torrents/resume";

        // The version proves the client answers; the torrent list proves the login works.
        public async Task<QbittorrentConnectionTestResult> TestAsync(QbittorrentConnection connection, CancellationToken ct)
        {
            try
            {
                var session = await LoginAsync(connection, ct);
                if (session is null) return new QbittorrentConnectionTestResult(false, null);

                using var response = await SendAsync(connection, session, HttpMethod.Get, VersionPath, null, ct);
                var version = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
                return new QbittorrentConnectionTestResult(response.IsSuccessStatusCode, string.IsNullOrWhiteSpace(version) ? null : version.Trim());
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                return new QbittorrentConnectionTestResult(false, null);
            }
        }

        // Hands the torrent file to the client and waits until it knows it.
        public async Task<QbittorrentGrabResult> GrabAsync(QbittorrentConnection connection, QbittorrentGrabRequest request, CancellationToken ct)
        {
            var session = await LoginAsync(connection, ct);
            if (session is null) return new QbittorrentGrabResult(false, null, "acquisition_unavailable");

            // A unique tag is correlated with the torrent that qBittorrent accepted.
            // Never guess from a title or the most recently added torrent: another client may
            // add a different release at exactly the same time.
            var tag = "jularr-" + Guid.NewGuid().ToString("N");
            using (var tagResponse = await SendAsync(connection, session, HttpMethod.Post, "/api/v2/torrents/createTags",
                [new("tags", tag)], ct))
            {
                if (!tagResponse.IsSuccessStatusCode) return new QbittorrentGrabResult(false, null, "acquisition_grab_failed");
            }

            using (var response = await SendAsync(connection, session, HttpMethod.Post, AddPath,
                [new("urls", request.TorrentUrl.ToString()), new("savepath", request.SavePath), new("tags", tag)], ct))
            {
                // HTTP 200 can still contain "Fails." for an already-known or rejected torrent.
                if (!response.IsSuccessStatusCode || (await response.Content.ReadAsStringAsync(ct)).Trim().Equals("Fails.", StringComparison.OrdinalIgnoreCase))
                {
                    return new QbittorrentGrabResult(false, null, "acquisition_grab_failed");
                }
            }

            for (var attempt = 0; attempt < 20; attempt++)
            {
                var snapshot = await GetSnapshotAsync(connection, null, ct);
                var matches = snapshot.Torrents.Where(item => item.Tags.Contains(tag, StringComparer.Ordinal)).ToArray();
                if (matches.Length == 1 && !string.IsNullOrWhiteSpace(matches[0].Hash))
                {
                    return new QbittorrentGrabResult(true, matches[0].Hash);
                }

                if (matches.Length > 1) break; // Ambiguous data must never be assigned to an operation.
                await Task.Delay(500, ct);
            }

            return new QbittorrentGrabResult(false, null, "acquisition_grab_failed");
        }

        // The folder the client writes into; Jularr maps it to the storage it reads from.
        public async Task<string?> GetDefaultSavePathAsync(QbittorrentConnection connection, CancellationToken ct)
        {
            var session = await LoginAsync(connection, ct);
            if (session is null) return null;

            using var response = await SendAsync(connection, session, HttpMethod.Get, PreferencesPath, null, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.TryGetProperty("save_path", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        // The state of every torrent, or of the given ones.
        public async Task<QbittorrentSnapshot> GetSnapshotAsync(QbittorrentConnection connection, IReadOnlyCollection<string>? hashes, CancellationToken ct)
        {
            var session = await LoginAsync(connection, ct);
            if (session is null) throw new QbittorrentException("The torrent client did not accept the login.");

            var path = hashes is null or { Count: 0 } ? InfoPath : $"{InfoPath}?hashes={Uri.EscapeDataString(string.Join('|', hashes))}";
            using var response = await SendAsync(connection, session, HttpMethod.Get, path, null, ct);
            if (!response.IsSuccessStatusCode) throw new QbittorrentException("The torrent client did not answer the list of torrents.");

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var torrents = new List<QbittorrentTorrent>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                torrents.Add(ReadTorrent(item));
            }

            return new QbittorrentSnapshot(torrents);
        }

        public async Task<IReadOnlyList<QbittorrentFile>> GetFilesAsync(QbittorrentConnection connection, string hash, CancellationToken ct)
        {
            var session = await LoginAsync(connection, ct);
            if (session is null) throw new QbittorrentException("The torrent client did not accept the login.");
            using var response = await SendAsync(connection, session, HttpMethod.Get,
                "/api/v2/torrents/files?hash=" + Uri.EscapeDataString(hash), null, ct);
            if (!response.IsSuccessStatusCode) throw new QbittorrentException("Cannot list the torrent files.");

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return [.. document.RootElement.EnumerateArray().Select(item => new QbittorrentFile(
                ReadString(item, "name") ?? "",
                Math.Max(0, ReadLong(item, "size")),
                Math.Clamp(ReadDouble(item, "progress"), 0, 1),
                ReadLong(item, "priority") > 0))];
        }

        public async Task RemoveAsync(QbittorrentConnection connection, string hash, bool deleteFiles, CancellationToken ct)
        {
            var session = await LoginAsync(connection, ct);
            if (session is null) return;
            using var response = await SendAsync(connection, session, HttpMethod.Post, DeletePath,
                [new("hashes", hash), new("deleteFiles", deleteFiles ? "true" : "false")], ct);
            if (!response.IsSuccessStatusCode) throw new QbittorrentException("The torrent client did not remove the torrent.");
        }

        public async Task SetPausedAsync(QbittorrentConnection connection, string hash, bool isPaused, CancellationToken ct)
        {
            var session = await LoginAsync(connection, ct);
            if (session is null) return;
            // qBittorrent 5 renamed pause/resume to stop/start.
            using var response = await SendAsync(connection, session, HttpMethod.Post,
                isPaused ? "/api/v2/torrents/stop" : "/api/v2/torrents/start", [new("hashes", hash)], ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                using var legacy = await SendAsync(connection, session, HttpMethod.Post, isPaused ? PausePath : ResumePath,
                    [new("hashes", hash)], ct);
                if (!legacy.IsSuccessStatusCode) throw new QbittorrentException("The torrent client did not change the torrent.");
                return;
            }

            if (!response.IsSuccessStatusCode) throw new QbittorrentException("The torrent client did not change the torrent.");
        }

        // The session cookie of a successful login; null when the client refuses the account.
        private async Task<string?> LoginAsync(QbittorrentConnection connection, CancellationToken ct)
        {
            using var response = await SendAsync(connection, null, HttpMethod.Post, LoginPath,
                [new("username", connection.UserName ?? ""), new("password", connection.Password ?? "")], ct, withoutSession: true);
            if (!response.IsSuccessStatusCode) return null;

            if (!response.Headers.TryGetValues("Set-Cookie", out var cookies)) return null;
            foreach (var cookie in cookies)
            {
                foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = part.Trim();
                    if (pair.StartsWith("SID=", StringComparison.Ordinal)) return pair;
                }
            }

            return null;
        }

        private Task<HttpResponseMessage> SendAsync(QbittorrentConnection connection, string? session, HttpMethod method, string path,
            IReadOnlyList<KeyValuePair<string, string>>? form, CancellationToken ct, bool withoutSession = false)
        {
            var request = new HttpRequestMessage(method, connection.BaseUrl.TrimEnd('/') + path);

            // The client checks the referer against the host; without it every call is refused.
            request.Headers.Referrer = new Uri(connection.BaseUrl.TrimEnd('/') + "/");
            if (session is not null) request.Headers.Add("Cookie", session);

            if (form is not null)
            {
                request.Content = new FormUrlEncodedContent(form);
            }

            return httpClient.SendAsync(request, ct);
        }

        private static QbittorrentTorrent ReadTorrent(JsonElement item)
        {
            var hash = ReadString(item, "hash") ?? "";
            var name = ReadString(item, "name") ?? "";
            var state = ReadString(item, "state") ?? "unknown";
            var progress = ReadDouble(item, "progress");
            var size = ReadLong(item, "size");
            var downloaded = ReadLong(item, "completed");
            var speed = ReadLong(item, "dlspeed");
            var seeds = (int)ReadLong(item, "num_complete");
            var peers = (int)ReadLong(item, "num_incomplete");
            var contentPath = ReadString(item, "content_path");
            var savePath = ReadString(item, "save_path");
            var addedAt = item.TryGetProperty("added_on", out var added) && added.ValueKind == JsonValueKind.Number ? added.GetInt64() : (long?)null;
            var tags = (ReadString(item, "tags") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return new QbittorrentTorrent(hash, name, state, progress, size, downloaded, speed, seeds, peers, contentPath, savePath, addedAt, tags: tags);
        }

        private static string? ReadString(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static long ReadLong(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

        private static double ReadDouble(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
    }
}
