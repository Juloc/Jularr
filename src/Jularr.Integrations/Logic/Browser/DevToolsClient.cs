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
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

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
        public async Task<string> SendAsync(string method, object? parameters, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _nextId);
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
            try
            {
                var payload = JsonSerializer.Serialize(new { id, method, @params = parameters });
                await _socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
                timeout.CancelAfter(CommandTimeout);
                return await completion.Task.WaitAsync(timeout.Token);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        // The value of a script run in the page.
        public async Task<string> EvaluateAsync(string expression, CancellationToken ct)
        {
            var answer = await SendAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = false }, ct);
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
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException)
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
    }
}
