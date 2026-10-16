using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Jularr.Integrations.Logic.Storage
{
    // A file as FFmpeg, ffprobe and fpcalc read it: the host path of a local folder, or for a direct share an HTTP address on the loopback interface
    // with a random token, seekable by ranges and valid while the tool runs.
    internal sealed partial class StorageStreamServer : IDisposable
    {
        private const int MaximumHeaderBytes = 16 * 1024;

        private readonly ConcurrentDictionary<string, (StorageFiles Files, string Path)> _files = new(StringComparer.Ordinal);
        private readonly Lock _start = new();
        private readonly CancellationTokenSource _stop = new();
        private TcpListener? _listener;

        public StorageInput Open(StorageFiles files, string path)
        {
            if (files is LocalStorageFiles local)
            {
                return new StorageInput(local.ResolveFile(path), null);
            }

            var port = Start();
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            _files[token] = (files, StoragePath.Check(path));

            // The name keeps the extension tools look at.
            return new StorageInput("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/" + token + "/" + Uri.EscapeDataString(StoragePath.Name(path)),
                () => _files.TryRemove(token, out _));
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener?.Stop();
            _stop.Dispose();
        }

        private int Start()
        {
            lock (_start)
            {
                if (_listener is null)
                {
                    var listener = new TcpListener(IPAddress.Loopback, 0);
                    listener.Start();
                    _listener = listener;
                    _ = Task.Run(() => AcceptAsync(listener));
                }

                return ((IPEndPoint)_listener.LocalEndpoint).Port;
            }
        }

        private async Task AcceptAsync(TcpListener listener)
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var request = await ReadHeaderAsync(stream);
                    var line = request?.Split("\r\n", 2)[0].Split(' ');
                    if (line is not [var method, var target, _] || method is not ("GET" or "HEAD") || target.Split('/', StringSplitOptions.RemoveEmptyEntries) is not [var token, ..]
                        || !_files.TryGetValue(token, out var file))
                    {
                        await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        return;
                    }

                    await using var content = await file.Files.OpenReadAsync(file.Path, _stop.Token);
                    var length = content.Length;
                    long start = 0, end = length - 1;
                    var range = RangePattern().Match(request!);
                    if (range.Success)
                    {
                        start = long.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture);
                        if (range.Groups[2].Success) end = Math.Min(end, long.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture));
                        if (start >= length || start > end)
                        {
                            await WriteAsync(stream, "HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */" + length + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                            return;
                        }
                    }

                    var count = Math.Max(0, end - start + 1);
                    var header = new StringBuilder(range.Success ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n")
                        .Append("Content-Type: application/octet-stream\r\nAccept-Ranges: bytes\r\nConnection: close\r\n")
                        .Append(CultureInfo.InvariantCulture, $"Content-Length: {count}\r\n");
                    if (range.Success) header.Append(CultureInfo.InvariantCulture, $"Content-Range: bytes {start}-{end}/{length}\r\n");
                    await WriteAsync(stream, header.Append("\r\n").ToString());
                    if (method == "HEAD")
                    {
                        return;
                    }

                    content.Seek(start, SeekOrigin.Begin);
                    var buffer = new byte[256 * 1024];
                    while (count > 0)
                    {
                        var read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), _stop.Token);
                        if (read == 0) break;
                        await stream.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                        count -= read;
                    }
                }
                catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException
                    or UnauthorizedAccessException or Data.Nas.NasMountException)
                {
                    // The tool went away or the share did; the tool reports what it missed.
                }
            }
        }

        private static async Task<string?> ReadHeaderAsync(NetworkStream stream)
        {
            var bytes = new List<byte>();
            var buffer = new byte[1];
            while (bytes.Count < MaximumHeaderBytes && await stream.ReadAsync(buffer) == 1)
            {
                bytes.Add(buffer[0]);
                if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
                {
                    return Encoding.ASCII.GetString([.. bytes]);
                }
            }

            return null;
        }

        private static Task WriteAsync(NetworkStream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

        [GeneratedRegex(@"^Range:\s*bytes=(\d+)-(\d+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
        private static partial Regex RangePattern();
    }

    // Where a tool finds the file; disposing ends the address.
    internal sealed class StorageInput(string location, Action? release) : IDisposable
    {
        public string Location { get; } = location;

        public void Dispose() => release?.Invoke();
    }
}
