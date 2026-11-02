using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace Jularr.Integrations.Logic.Torrent
{
    // HTTP(S) tracker announces (BEP 3, compact BEP 23). Jularr reports nothing uploaded and offers no port it listens on.
    internal static class TorrentTrackers
    {
        private const int MaximumResponse = 1024 * 1024;

        public static async Task<IReadOnlyList<(string Host, int Port)>> AnnounceAsync(HttpClient client, Uri tracker, byte[] infoHash, byte[] peerId, long downloaded, long left,
            CancellationToken ct)
        {
            if (tracker.Scheme is not ("http" or "https")) return [];
            var query = "info_hash=" + Escape(infoHash) + "&peer_id=" + Escape(peerId) + "&port=6881&uploaded=0&downloaded="
                + downloaded.ToString(CultureInfo.InvariantCulture) + "&left=" + left.ToString(CultureInfo.InvariantCulture) + "&compact=1&numwant=80&event=started";
            var address = new Uri(tracker.GetLeftPart(UriPartial.Path) + (tracker.Query.Length > 1 ? tracker.Query + "&" : "?") + query);
            using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            await body.CopyToAsync(buffer, ct);
            if (buffer.Length > MaximumResponse) throw new InvalidDataException("Tracker answer is too large.");
            return Peers(buffer.ToArray());
        }

        public static IReadOnlyList<(string Host, int Port)> Peers(byte[] answer)
        {
            if (Bencode.Decode(answer) is not Bencode.Dictionary root) throw new InvalidDataException("Invalid tracker answer.");
            if (root.TryGetValue("failure reason", out var failure)) throw new InvalidDataException("Tracker refused: " + Bencode.Text(failure));
            var peers = new List<(string, int)>();
            switch (root.GetValueOrDefault("peers"))
            {
                case byte[] compact when compact.Length % 6 == 0:
                    for (var offset = 0; offset < compact.Length; offset += 6)
                    {
                        peers.Add((new IPAddress(compact.AsSpan(offset, 4)).ToString(), BinaryPrimitives.ReadUInt16BigEndian(compact.AsSpan(offset + 4))));
                    }

                    break;
                case List<object> list:
                    foreach (var entry in list.OfType<Bencode.Dictionary>())
                    {
                        if (entry.GetValueOrDefault("ip") is byte[] ip && entry.GetValueOrDefault("port") is long port and > 0 and < 65536) peers.Add((Encoding.ASCII.GetString(ip), (int)port));
                    }

                    break;
            }

            if (root.GetValueOrDefault("peers6") is byte[] six && six.Length % 18 == 0)
            {
                for (var offset = 0; offset < six.Length; offset += 18)
                {
                    peers.Add((new IPAddress(six.AsSpan(offset, 16)).ToString(), BinaryPrimitives.ReadUInt16BigEndian(six.AsSpan(offset + 16))));
                }
            }

            return [.. peers.Where(peer => peer.Item2 > 0)];
        }

        private static string Escape(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 3);
            foreach (var value in bytes)
            {
                if (value is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~') text.Append((char)value);
                else text.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }
    }
}
