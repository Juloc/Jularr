using System.Globalization;
using System.Numerics;
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
            var output = new byte[body.Length];
            if (!TryDecode(body, output, out var offset, out var length, out var intact))
            {
                throw new UsenetException(UsenetFailure.InvalidResponse, "The article is not yEnc encoded.");
            }

            return new YencPart(offset, output[..length], intact);
        }

        // Decodes into output, which holds at least as many bytes as the body; false when the body is not yEnc.
        public static bool TryDecode(ReadOnlySpan<byte> body, Span<byte> output, out long offset, out int length, out bool intact)
        {
            offset = 0;
            length = 0;
            intact = false;
            var multipart = false;
            uint? expected = null;
            var started = false;
            while (!body.IsEmpty)
            {
                var newline = body.IndexOf((byte)'\n');
                var line = newline < 0 ? body : body[..newline];
                body = newline < 0 ? [] : body[(newline + 1)..];
                if (!line.IsEmpty && line[^1] == (byte)'\r')
                {
                    line = line[..^1];
                }

                if (line.Length >= 2 && line[0] == (byte)'=' && line[1] == (byte)'y')
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
                        expected = uint.TryParse(Field(line, "pcrc32") ?? (multipart ? null : Field(line, "crc32")), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                            out var crc) ? crc : null;
                        break;
                    }
                }

                if (!started)
                {
                    continue;
                }

                // An escaped byte is shifted by a further 64; runs without escapes are shifted in one go.
                while (!line.IsEmpty)
                {
                    var escape = line.IndexOf((byte)'=');
                    var run = escape < 0 ? line.Length : escape;
                    Shift(line[..run], output.Slice(length, run));
                    length += run;
                    if (escape < 0)
                    {
                        break;
                    }

                    if (escape + 1 < line.Length)
                    {
                        output[length++] = (byte)(line[escape + 1] - 64 - 42);
                        line = line[(escape + 2)..];
                    }
                    else
                    {
                        output[length++] = (byte)('=' - 42);
                        line = [];
                    }
                }
            }

            if (!started)
            {
                return false;
            }

            intact = expected is { } crc32 && Checksum(output[..length]) == crc32;
            return true;
        }

        // Standard CRC-32, eight bytes per step.
        public static uint Checksum(ReadOnlySpan<byte> bytes)
        {
            var crc = 0xFFFFFFFFu;
            var table = Table.AsSpan();
            while (bytes.Length >= 8)
            {
                var low = crc ^ (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16 | bytes[3] << 24);
                var high = (uint)(bytes[4] | bytes[5] << 8 | bytes[6] << 16 | bytes[7] << 24);
                crc = table[7 * 256 + (int)(low & 0xFF)] ^ table[6 * 256 + (int)(low >> 8 & 0xFF)] ^ table[5 * 256 + (int)(low >> 16 & 0xFF)] ^ table[4 * 256 + (int)(low >> 24)]
                    ^ table[3 * 256 + (int)(high & 0xFF)] ^ table[2 * 256 + (int)(high >> 8 & 0xFF)] ^ table[256 + (int)(high >> 16 & 0xFF)] ^ table[(int)(high >> 24)];
                bytes = bytes[8..];
            }

            foreach (var value in bytes)
            {
                crc = table[(int)((crc ^ value) & 0xFF)] ^ (crc >> 8);
            }

            return ~crc;
        }

        private static void Shift(ReadOnlySpan<byte> source, Span<byte> target)
        {
            var index = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var shift = new Vector<byte>(42);
                for (; index <= source.Length - Vector<byte>.Count; index += Vector<byte>.Count)
                {
                    (new Vector<byte>(source[index..]) - shift).CopyTo(target[index..]);
                }
            }

            for (; index < source.Length; index++)
            {
                target[index] = (byte)(source[index] - 42);
            }
        }

        // Header values are "key=value".
        private static string? Field(ReadOnlySpan<byte> line, string key)
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
            var table = new uint[8 * 256];
            for (var index = 0u; index < 256; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }

                table[index] = value;
            }

            for (var index = 0; index < 256; index++)
            {
                for (var slice = 1; slice < 8; slice++)
                {
                    var previous = table[(slice - 1) * 256 + index];
                    table[slice * 256 + index] = table[(int)(previous & 0xFF)] ^ (previous >> 8);
                }
            }

            return table;
        }
    }
}
