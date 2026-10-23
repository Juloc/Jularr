using System.Globalization;
using System.Text;
using Jularr.Integrations.Data.Usenet;

namespace Jularr.Integrations.Logic.Usenet
{
    // yEnc articles: =ybegin, an optional =ypart, encoded lines and =yend with the part checksum.
    internal static class YencDecoder
    {
        private static readonly uint[] Table = CreateTable();

        public static YencPart Decode(byte[] body)
        {
            long offset = 0;
            var multipart = false;
            uint? expected = null;
            var data = new MemoryStream(body.Length);
            var started = false;
            foreach (var line in Lines(body))
            {
                if (line.StartsWith("=ybegin "u8))
                {
                    started = true;
                    continue;
                }

                if (line.StartsWith("=ypart "u8))
                {
                    multipart = true;
                    offset = long.TryParse(Field(line, "begin"), NumberStyles.None, CultureInfo.InvariantCulture, out var begin) && begin > 0 ? begin - 1 : 0;
                    continue;
                }

                if (line.StartsWith("=yend"u8))
                {
                    expected = uint.TryParse(Field(line, "pcrc32") ?? (multipart ? null : Field(line, "crc32")), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var crc) ? crc : null;
                    break;
                }

                if (!started)
                {
                    continue;
                }

                // An escaped byte is shifted by a further 64.
                for (var index = 0; index < line.Length; index++)
                {
                    var value = line[index];
                    if (value == (byte)'=' && index + 1 < line.Length)
                    {
                        value = (byte)(line[++index] - 64);
                    }

                    data.WriteByte((byte)(value - 42));
                }
            }

            if (!started)
            {
                throw new UsenetException(UsenetFailure.InvalidResponse, "The article is not yEnc encoded.");
            }

            var bytes = data.ToArray();
            return new YencPart(offset, bytes, expected is { } crc32 && Checksum(bytes) == crc32);
        }

        public static uint Checksum(ReadOnlySpan<byte> bytes)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in bytes)
            {
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            return ~crc;
        }

        private static IEnumerable<byte[]> Lines(byte[] body)
        {
            var start = 0;
            for (var index = 0; index <= body.Length; index++)
            {
                if (index == body.Length || body[index] == (byte)'\n')
                {
                    var end = index > start && body[index - 1] == (byte)'\r' ? index - 1 : index;
                    if (end > start || index < body.Length)
                    {
                        yield return body[start..end];
                    }

                    start = index + 1;
                }
            }
        }

        // Header values are "key=value".
        private static string? Field(byte[] line, string key)
        {
            var text = Encoding.Latin1.GetString(line);
            var position = text.IndexOf(" " + key + "=", StringComparison.Ordinal);
            if (position < 0)
            {
                return null;
            }

            var value = text[(position + key.Length + 2)..];
            return value.Split(' ', 2)[0].Trim();
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (var index = 0u; index < 256; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }
    }
}
