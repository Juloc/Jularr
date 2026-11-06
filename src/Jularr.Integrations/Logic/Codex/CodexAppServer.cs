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

        // The thread's whole instruction: everything else Codex would load for a coding agent (tools, plugins, apps, notes on the environment) is switched off, so the
        // request carries little more than the text. Settings a Codex version does not know are ignored by it.
        private const string BaseInstructions = "You are a literary translator. Follow the user's instructions exactly.";

        private static readonly string[] OffFeatures =
        [
            "apps", "browser_use", "browser_use_external", "computer_use", "goals", "image_generation", "multi_agent", "plugins", "hooks", "memories", "code_mode_host", "shell_tool", "unified_exec",
            "view_image", "skill_search", "sleep_tool", "tool_suggest", "collaboration_modes", "in_app_browser", "mentions_v2", "workspace_dependencies", "worktrees", "tool_call_mcp_elicitation",
            "write_stdin_approval"
        ];

        // One prompt in an ephemeral, read-only thread on a short `codex app-server`; the answer's text arrives in pieces through `onText`.
        public static async Task<CodexAnswer> StreamAsync(ProcessStartInfo start, string workPath, string model, string? effort, string prompt, Action<string> onText, TimeSpan limit, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(limit);
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
                    var features = OffFeatures.ToDictionary(name => name, _ => false);
                    var thread = await session.RequestAsync(2, "thread/start", new
                    {
                        model, ephemeral = true, cwd = workPath, sandbox = "read-only", approvalPolicy = "never", baseInstructions = BaseInstructions,
                        config = new Dictionary<string, object>
                        {
                            ["features"] = features, ["web_search"] = "disabled", ["include_apply_patch_tool"] = false, ["include_permissions_instructions"] = false,
                            ["include_environment_context"] = false, ["include_apps_instructions"] = false
                        }
                    }, timeout.Token);
                    var threadId = thread.TryGetProperty("thread", out var created) ? Text(created, "id") : null;
                    if (threadId is null) throw new InvalidDataException("Codex did not start a thread.");

                    await session.SendAsync(new
                    {
                        id = 3, method = "turn/start",
                        @params = new { threadId, input = new object[] { new { type = "text", text = prompt, text_elements = Array.Empty<object>() } }, model, effort }
                    }, timeout.Token);
                    return await session.ReadTurnAsync(3, onText, timeout.Token);
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

            // Reads the turn started by request `id` until it ends: the pieces of the answer go to `onText`; a failed turn is an exception (the plan's limit its own).
            public async Task<CodexAnswer> ReadTurnAsync(int id, Action<string> onText, CancellationToken ct)
            {
                var text = new System.Text.StringBuilder();
                long tokens = 0;
                string? failure = null;
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (root.TryGetProperty("id", out var answered))
                    {
                        if (root.TryGetProperty("method", out _))
                        {
                            await SendAsync(new { id = answered, error = new { code = MethodNotFound, message = "Not supported by Jularr." } }, ct);
                        }
                        else if (answered.TryGetInt32(out var number) && number == id && root.TryGetProperty("error", out var error))
                        {
                            throw CodexCli.PlanLimitIn(Text(error, "message")) ?? (Exception)new InvalidDataException("Codex did not start the turn.");
                        }

                        continue;
                    }

                    if (!root.TryGetProperty("method", out var method) || !root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object) continue;
                    switch (method.GetString())
                    {
                        case "item/agentMessage/delta":
                            if (Text(parameters, "delta") is { Length: > 0 } piece)
                            {
                                text.Append(piece);
                                onText(piece);
                            }

                            break;
                        case "thread/tokenUsage/updated":
                            if (parameters.TryGetProperty("tokenUsage", out var usage) && usage.TryGetProperty("last", out var last))
                            {
                                tokens = Number(last, "inputTokens") + Number(last, "outputTokens");
                            }

                            break;
                        case "error":
                            if (parameters.TryGetProperty("error", out var problem) && Text(problem, "message") is { } message)
                            {
                                if (CodexCli.PlanLimitIn(message) is { } limit) throw limit;
                                failure = message;
                            }

                            break;
                        case "turn/completed":
                            var turn = parameters.TryGetProperty("turn", out var ended) ? ended : default;
                            var status = Text(turn, "status");
                            if (status != "completed")
                            {
                                var reason = turn.ValueKind == JsonValueKind.Object && turn.TryGetProperty("error", out var turnError) ? Text(turnError, "message") : failure;
                                throw CodexCli.PlanLimitIn(reason) ?? (Exception)new InvalidDataException("Codex did not answer.");
                            }

                            var answer = text.ToString().Trim();
                            if (answer.Length == 0) throw new InvalidDataException("Codex answered with nothing.");
                            return new CodexAnswer(answer, tokens);
                    }
                }

                throw new IOException("Codex app-server ended.");
            }

            private static long Number(JsonElement value, string name) =>
                value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number) && number > 0 ? number : 0;

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
