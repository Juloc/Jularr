using System.Buffers.Binary;
using System.Text.Json;

namespace Jularr.Integrations.Logic.Nas
{
    internal static class NasMountProtocol
    {
        public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
        {
            // Bound each local message.
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, ct);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length is < 1 or > 65536)
            {
                throw new InvalidDataException("Invalid mount message length.");
            }

            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, ct);
            return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("Missing mount message.");
        }

        public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(value);
            if (payload.Length > 65536)
            {
                throw new InvalidDataException("Mount message too large.");
            }

            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(payload, ct);
            await stream.FlushAsync(ct);
        }
    }
}
