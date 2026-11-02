using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Jularr.Integrations.Data.Usenet;

namespace Jularr.Integrations.Logic.Usenet
{
    // One authenticated NNTP session that reads article bodies by message id.
    internal sealed class NntpConnection : IAsyncDisposable
    {
        private const int MaximumArticleBytes = 16 * 1024 * 1024;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
        private readonly TcpClient _client;
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _start;
        private int _end;

        // A session left mid-answer cannot be used again.
        public bool IsBroken { get; private set; }

        private NntpConnection(TcpClient client, Stream stream)
        {
            _client = client;
            _stream = stream;
        }

        public static async Task<NntpConnection> OpenAsync(UsenetServer server, CancellationToken ct)
        {
            var client = new TcpClient { NoDelay = true };
            try
            {
                using var timeout = Bound(ct);
                await client.ConnectAsync(server.Host, server.Port, timeout.Token);
                Stream stream = client.GetStream();
                if (server.UseTls)
                {
                    var tls = new SslStream(stream, false);
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = server.Host }, timeout.Token);
                    stream = tls;
                }

                var connection = new NntpConnection(client, stream);
                var greeting = (await connection.ReadStatusAsync(timeout.Token)).Code;
                if (greeting == 502)
                {
                    // Providers answer 502 when the account already holds its allowed number of connections.
                    throw new UsenetException(UsenetFailure.ConnectionLimit, "The news server allows no more connections.");
                }

                if (greeting is not (200 or 201))
                {
                    throw new UsenetException(UsenetFailure.Unavailable, "The news server refused the connection.");
                }

                // Credentials are only sent when the server has them configured; a line break would start another command.
                if (server.Username is { Length: > 0 } user)
                {
                    if (user.AsSpan().IndexOfAny('\r', '\n') >= 0 || server.Password is { } secret && secret.AsSpan().IndexOfAny('\r', '\n') >= 0)
                    {
                        throw new UsenetException(UsenetFailure.Unauthorized, "The login contains a line break.");
                    }

                    var status = await connection.CommandAsync("AUTHINFO USER " + user, timeout.Token);
                    if (status.Code == 381)
                    {
                        status = await connection.CommandAsync("AUTHINFO PASS " + server.Password, timeout.Token);
                    }

                    if (status.Code == 502)
                    {
                        throw new UsenetException(UsenetFailure.ConnectionLimit, "The news server allows no more connections.");
                    }

                    if (status.Code != 281)
                    {
                        throw new UsenetException(UsenetFailure.Unauthorized, "The news server rejected the login.");
                    }
                }

                return connection;
            }
            catch (Exception exception) when (exception is SocketException or IOException or AuthenticationException || exception is OperationCanceledException && !ct.IsCancellationRequested)
            {
                client.Dispose();
                throw new UsenetException(UsenetFailure.Unavailable, "The news server could not be reached.", exception);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        // A cheap command that proves the session works.
        public async Task CheckAsync(CancellationToken ct)
        {
            using var timeout = Bound(ct);
            if ((await CommandAsync("DATE", timeout.Token)).Code != 111)
            {
                throw new UsenetException(UsenetFailure.InvalidResponse, "The news server did not answer DATE.");
            }
        }

        public async Task<byte[]> ReadBodyAsync(string messageId, CancellationToken ct)
        {
            using var timeout = Bound(ct);
            try
            {
                var status = await CommandAsync("BODY <" + messageId + ">", timeout.Token);
                switch (status.Code)
                {
                    case 222:
                        break;
                    case 423 or 430:
                        throw new UsenetException(UsenetFailure.ArticleMissing, "The article is not available.");
                    case 502:
                        throw new UsenetException(UsenetFailure.ConnectionLimit, "The news server allows no more connections.");
                    case 480 or 481:
                        throw new UsenetException(UsenetFailure.Unauthorized, "The news server refused the article.");
                    default:
                        throw new UsenetException(UsenetFailure.Unavailable, "The news server answered " + status.Code.ToString(CultureInfo.InvariantCulture) + ".");
                }

                // Lines until the lone dot; a leading doubled dot is unstuffed.
                var body = new MemoryStream();
                while (true)
                {
                    var line = await ReadLineAsync(timeout.Token);
                    if (line.Length == 1 && line[0] == (byte)'.')
                    {
                        return body.ToArray();
                    }

                    var start = line.Length >= 2 && line[0] == (byte)'.' && line[1] == (byte)'.' ? 1 : 0;
                    body.Write(line, start, line.Length - start);
                    body.Write("\r\n"u8);
                    if (body.Length > MaximumArticleBytes)
                    {
                        IsBroken = true;
                        throw new UsenetException(UsenetFailure.InvalidResponse, "The article is too large.");
                    }
                }
            }
            catch (Exception exception) when (exception is IOException || exception is OperationCanceledException && !ct.IsCancellationRequested)
            {
                IsBroken = true;
                throw new UsenetException(UsenetFailure.Unavailable, "The news server stopped answering.", exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _stream.WriteAsync("QUIT\r\n"u8.ToArray(), timeout.Token);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }

            await _stream.DisposeAsync();
            _client.Dispose();
        }

        private async Task<(int Code, string Text)> CommandAsync(string command, CancellationToken ct)
        {
            await _stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\r\n"), ct);
            await _stream.FlushAsync(ct);
            return await ReadStatusAsync(ct);
        }

        private async Task<(int Code, string Text)> ReadStatusAsync(CancellationToken ct)
        {
            var line = Encoding.ASCII.GetString(await ReadLineAsync(ct));
            return line.Length >= 3 && int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var code)
                ? (code, line)
                : throw new UsenetException(UsenetFailure.InvalidResponse, "The news server sent no status.");
        }

        private async Task<byte[]> ReadLineAsync(CancellationToken ct)
        {
            var line = new MemoryStream();
            while (true)
            {
                if (_start == _end)
                {
                    _start = 0;
                    _end = await _stream.ReadAsync(_buffer, ct);
                    if (_end == 0)
                    {
                        throw new IOException("The news server closed the connection.");
                    }
                }

                var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                var stop = newline < 0 ? _end : newline;
                line.Write(_buffer, _start, stop - _start);
                _start = newline < 0 ? _end : newline + 1;
                if (newline >= 0)
                {
                    var bytes = line.ToArray();
                    return bytes.Length > 0 && bytes[^1] == (byte)'\r' ? bytes[..^1] : bytes;
                }

                if (line.Length > MaximumArticleBytes)
                {
                    throw new UsenetException(UsenetFailure.InvalidResponse, "The news server sent an endless line.");
                }
            }
        }

        private static CancellationTokenSource Bound(CancellationToken ct)
        {
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            return timeout;
        }
    }
}
