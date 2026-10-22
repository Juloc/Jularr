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

            using var response = await SendAsync(connection, session, HttpMethod.Post, AddPath,
                [new("urls", request.TorrentUrl.ToString()), new("savepath", request.SavePath)], ct);
            if (!response.IsSuccessStatusCode) return new QbittorrentGrabResult(false, null, "acquisition_grab_failed");

            // The client answers "Ok." without naming the torrent; its list does.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var snapshot = await GetSnapshotAsync(connection, null, ct);
                var torrent = snapshot.Torrents.FirstOrDefault(item => item.Name == request.Title) ??
                    snapshot.Torrents.OrderByDescending(item => item.AddedAt).FirstOrDefault();
                if (torrent is not null) return new QbittorrentGrabResult(true, torrent.Hash);
                await Task.Delay(500, ct);
            }

            return new QbittorrentGrabResult(false, null, "acquisition_grab_failed");
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
            using var response = await SendAsync(connection, session, HttpMethod.Post, isPaused ? PausePath : ResumePath, [new("hashes", hash)], ct);
            if (!response.IsSuccessStatusCode) throw new QbittorrentException("The torrent client did not change the torrent.");
        }

        // The session cookie of a successful login; null when the client refuses the account.
        private async Task<string?> LoginAsync(QbittorrentConnection connection, CancellationToken ct)
        {
            using var response = await SendAsync(connection, null, HttpMethod.Post, LoginPath,
                [new("username", connection.UserName ?? ""), new("password", connection.Password ?? "")], ct, withoutSession: true);
            if (!response.IsSuccessStatusCode) return null;

            foreach (var cookie in response.Headers.GetValues("Set-Cookie"))
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
            return new QbittorrentTorrent(hash, name, state, progress, size, downloaded, speed, seeds, peers, contentPath, savePath, addedAt);
        }

        private static string? ReadString(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static long ReadLong(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

        private static double ReadDouble(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
    }
}
