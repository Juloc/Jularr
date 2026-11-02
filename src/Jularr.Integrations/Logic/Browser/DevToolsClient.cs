using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Jularr.Integrations.Data.Browser;

namespace Jularr.Integrations.Logic.Browser
{
    // The DevTools protocol of one browser page: commands go out, answers and events come back.
    internal sealed class DevToolsClient : IAsyncDisposable
    {
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

        private readonly ClientWebSocket _socket = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pending = new();
        private readonly Func<string, JsonElement, Task> _onEvent;
        private readonly CancellationTokenSource _stop = new();
        private int _nextId;

        public DevToolsClient(Func<string, JsonElement, Task>? onEvent = null) => _onEvent = onEvent ?? ((_, _) => Task.CompletedTask);

        public async Task ConnectAsync(string webSocketUrl, CancellationToken ct)
        {
            await _socket.ConnectAsync(new Uri(webSocketUrl), ct);
            _ = Task.Run(ReceiveAsync);
        }

        // The raw answer of the command.
        public async Task<string> SendAsync(string method, object? parameters, CancellationToken ct, string? sessionId = null)
        {
            var id = Interlocked.Increment(ref _nextId);
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
            try
            {
                var payload = sessionId is null
                    ? JsonSerializer.Serialize(new { id, method, @params = parameters })
                    : JsonSerializer.Serialize(new { id, method, @params = parameters, sessionId });
                await _socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
                timeout.CancelAfter(CommandTimeout);
                return await completion.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SourceBrowserException($"The browser did not answer '{method}'.");
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        // A page of its own, so several addresses can be read at the same time.
        public async Task<(string Target, string Session)> OpenPageAsync(CancellationToken ct)
        {
            var created = await SendAsync("Target.createTarget", new { url = "about:blank", background = true }, ct);
            using var document = JsonDocument.Parse(created);
            var targetId = document.RootElement.GetProperty("result").GetProperty("targetId").GetString()!;
            try
            {
                var attached = await SendAsync("Target.attachToTarget", new { targetId, flatten = true }, ct);
                using var answer = JsonDocument.Parse(attached);
                var sessionId = answer.RootElement.GetProperty("result").GetProperty("sessionId").GetString()!;
                return (targetId, sessionId);
            }
            catch
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await ClosePageAsync(targetId, cleanup.Token);
                throw;
            }
        }

        public async Task ClosePageAsync(string targetId, CancellationToken ct)
        {
            try
            {
                await SendAsync("Target.closeTarget", new { targetId }, ct);
            }
            catch (Exception exception) when (exception is SourceBrowserException or OperationCanceledException)
            {
                // The page is gone anyway.
            }
        }

        // The value of a script run in the page.
        public async Task<string> EvaluateAsync(string expression, CancellationToken ct, string? sessionId = null)
        {
            var answer = await SendAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = false }, ct, sessionId);
            using var document = JsonDocument.Parse(answer);
            if (!document.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("result", out var value)
                || !value.TryGetProperty("value", out var content))
            {
                return "";
            }

            return content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : content.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _socket.Dispose();
        }

        private async Task ReceiveAsync()
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var builder = new StringBuilder();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _stop.Token);
                        builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);

                    await HandleAsync(builder.ToString());
                }
            }
            catch (Exception)
            {
                // The browser is gone; the waiting commands fail with it.
            }
            finally
            {
                foreach (var completion in _pending.Values)
                {
                    completion.TrySetException(new SourceBrowserException("The browser closed the connection."));
                }

                _pending.Clear();
            }
        }

        private async Task HandleAsync(string message)
        {
            try
            {
                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var id) && id.TryGetInt32(out var number))
                {
                    if (_pending.TryRemove(number, out var completion))
                    {
                        completion.TrySetResult(message);
                    }

                    return;
                }

                if (root.TryGetProperty("method", out var method))
                {
                    await _onEvent(method.GetString() ?? "", root);
                }
            }
            catch (JsonException)
            {
                // A message that is no answer is left alone.
            }
        }
    }
}
