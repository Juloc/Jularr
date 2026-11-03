using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Jularr.Integrations.Data.Codex;
using Jularr.Integrations.Data.Common;

namespace Jularr.Integrations.Logic.Codex
{
    // One short `codex app-server` session (JSON lines over stdio) that asks for the account's models and plan usage and ends. The server never grants
    // anything the app-server asks for; it only reads.
    internal static class CodexAppServer
    {
        private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(25);
        private const int MethodNotFound = -32601;

        public static async Task<CodexCatalog> ReadCatalogAsync(ProcessStartInfo start, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SessionTimeout);
            Process process;
            try
            {
                process = Process.Start(start) ?? throw new Win32Exception();
            }
            catch (Win32Exception)
            {
                throw new ProviderUnavailableException();
            }

            using (process)
            {
                // The app-server writes diagnostics to stderr that may name the account; they are read away and dropped.
                var drain = Task.Run(async () =>
                {
                    try { while (await process.StandardError.ReadLineAsync(timeout.Token) is not null) { } }
                    catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
                });
                try
                {
                    var session = new Session(process);
                    await session.RequestAsync(1, "initialize", new { clientInfo = new { name = "jularr", title = "Jularr", version = "1" }, capabilities = new { experimentalApi = false } }, timeout.Token);
                    await session.SendAsync(new { method = "initialized", @params = new { } }, timeout.Token);

                    var models = new List<CodexModel>();
                    string? cursor = null;
                    for (var page = 0; page < 10; page++)
                    {
                        var result = await session.RequestAsync(10 + page, "model/list", new { cursor, limit = 100, includeHidden = false }, timeout.Token);
                        models.AddRange(ParseModels(result));
                        cursor = Text(result, "nextCursor");
                        if (cursor is null) break;
                    }

                    // A Codex version without rate limits, or an account without them, still lists its models.
                    CodexUsage? usage = null;
                    try
                    {
                        usage = ParseUsage(await session.RequestAsync(100, "account/rateLimits/read", null, timeout.Token));
                    }
                    catch (CodexRequestException)
                    {
                    }

                    return new CodexCatalog([.. models], usage);
                }
                catch (Exception exception) when (exception is CodexRequestException or IOException or JsonException or InvalidOperationException
                    || exception is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    throw new ProviderUnavailableException();
                }
                finally
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    await timeout.CancelAsync();
                    await drain;
                }
            }
        }

        internal static CodexModel[] ParseModels(JsonElement result)
        {
            if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
            var models = new List<CodexModel>();
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || entry.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
                var id = Text(entry, "model") ?? Text(entry, "id");
                if (id is null) continue;
                var efforts = entry.TryGetProperty("supportedReasoningEfforts", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(effort => Text(effort, "reasoningEffort")).OfType<string>().ToArray() : [];
                models.Add(new CodexModel(id, Text(entry, "displayName") ?? id, efforts, Text(entry, "defaultReasoningEffort"), entry.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True));
            }

            return [.. models];
        }

        internal static CodexUsage? ParseUsage(JsonElement result)
        {
            // The plan's own bucket ("codex") when the answer has several, else the single one.
            JsonElement bucket = default;
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in byId.EnumerateObject())
                {
                    if (bucket.ValueKind == JsonValueKind.Undefined || property.Name == "codex") bucket = property.Value;
                }
            }

            if (bucket.ValueKind != JsonValueKind.Object && !(result.ValueKind == JsonValueKind.Object && result.TryGetProperty("rateLimits", out bucket) && bucket.ValueKind == JsonValueKind.Object)) return null;
            var windows = new List<CodexUsageWindow>();
            foreach (var name in new[] { "primary", "secondary" })
            {
                if (!bucket.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number) continue;
                var minutes = window.TryGetProperty("windowDurationMins", out var length) && length.ValueKind == JsonValueKind.Number && length.TryGetInt32(out var value) && value > 0 ? value : (int?)null;
                var reset = window.TryGetProperty("resetsAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var seconds) && seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null;
                windows.Add(new CodexUsageWindow(Math.Clamp(used.GetDouble(), 0, 100), minutes, reset));
            }

            return new CodexUsage(Text(bucket, "planType"), [.. windows]);
        }

        private static string? Text(JsonElement value, string name) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

        private sealed class CodexRequestException(int code) : Exception("Codex app-server request failed.")
        {
            public int Code { get; } = code;
        }

        private sealed class Session(Process process)
        {
            public async Task SendAsync(object message, CancellationToken ct)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct);
                await process.StandardInput.FlushAsync(ct);
            }

            // Sends one request and reads lines until its answer; notifications are skipped and the server's own requests are refused.
            public async Task<JsonElement> RequestAsync(int id, string method, object? parameters, CancellationToken ct)
            {
                await SendAsync(new { id, method, @params = parameters ?? new { } }, ct);
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var answered)) continue;
                    if (root.TryGetProperty("method", out _))
                    {
                        await SendAsync(new { id = answered, error = new { code = MethodNotFound, message = "Not supported by Jularr." } }, ct);
                        continue;
                    }

                    if (!answered.TryGetInt32(out var number) || number != id) continue;
                    if (root.TryGetProperty("error", out var error))
                        throw new CodexRequestException(error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) && code.TryGetInt32(out var parsed) ? parsed : 0);
                    return root.TryGetProperty("result", out var result) ? result.Clone() : default;
                }

                throw new IOException("Codex app-server ended.");
            }
        }
    }
}
