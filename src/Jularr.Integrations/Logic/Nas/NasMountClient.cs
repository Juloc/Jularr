using System.Net.Sockets;
using System.Text.Json;
using Jularr.Integrations.Data.Nas;

namespace Jularr.Integrations.Logic.Nas
{
    internal sealed class NasMountClient
    {
        public string? SocketPath { get; set; }
        public bool IsConfigured => !string.IsNullOrWhiteSpace(SocketPath);

        public async Task<NasMountResponse> ExecuteAsync(NasMountRequest request, CancellationToken ct)
        {
            if (!IsConfigured)
            {
                throw new NasMountException("storage_helper_unavailable");
            }

            // Bound privileged communication.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), timeout.Token);
                using var stream = new NetworkStream(socket);
                await NasMountProtocol.WriteAsync(stream, request, timeout.Token);
                var response = await NasMountProtocol.ReadAsync<NasMountResponse>(stream, timeout.Token);
                if (response.ErrorCode is { } code)
                {
                    throw new NasMountException(code is "storage_busy" or "storage_configuration_changed" ? code : "storage_unavailable");
                }

                return response;
            }
            catch (Exception exception) when (exception is SocketException or IOException or JsonException
                || exception is OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new NasMountException("storage_helper_unavailable");
            }
        }
    }
}
