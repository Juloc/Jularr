using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jularr.Backend.Logic.Reading
{
    // The pixel size of a picture from its header; none when the format is unknown or the header is cut.
    internal static partial class ImageSize
    {
        [GeneratedRegex(@"viewBox\s*=\s*""\s*[-\d.]+[ ,]+[-\d.]+[ ,]+([\d.]+)[ ,]+([\d.]+)\s*""", RegexOptions.IgnoreCase)]
        private static partial Regex ViewBox();

        private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        public static (int Width, int Height)? Read(ReadOnlySpan<byte> data)
        {
            if (data.Length >= 24 && data[..8].SequenceEqual(PngSignature))
            {
                return Positive(BinaryPrimitives.ReadInt32BigEndian(data[16..]), BinaryPrimitives.ReadInt32BigEndian(data[20..]));
            }

            if (data.Length >= 10 && data[..3].SequenceEqual("GIF"u8))
            {
                return Positive(BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));
            }

            if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
            {
                return Jpeg(data);
            }

            if (data.Length >= 30 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
            {
                return WebP(data);
            }

            // A vector picture is sized by its view box.
            if (data.Length > 16 && data.Length < 4 * 1024 * 1024 && data.IndexOf("<svg"u8) is >= 0 and var start)
            {
                var head = Encoding.UTF8.GetString(data[start..Math.Min(data.Length, start + 2048)]);
                if (ViewBox().Match(head) is { Success: true } match && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
                    && double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
                {
                    return Positive((int)Math.Round(width), (int)Math.Round(height));
                }
            }

            return null;
        }

        private static (int, int)? Positive(int width, int height) => width > 0 && height > 0 && width <= 100_000 && height <= 100_000 ? (width, height) : null;

        private static (int, int)? Jpeg(ReadOnlySpan<byte> data)
        {
            // Segments until a start-of-frame marker.
            var position = 2;
            while (position + 9 < data.Length)
            {
                if (data[position] != 0xFF)
                {
                    position++;
                    continue;
                }

                var marker = data[position + 1];
                if (marker == 0xFF)
                {
                    position++;
                    continue;
                }

                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
                {
                    position += 2;
                    continue;
                }

                var length = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);
                if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                {
                    return Positive(BinaryPrimitives.ReadUInt16BigEndian(data[(position + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(data[(position + 5)..]));
                }

                position += 2 + length;
            }

            return null;
        }

        private static (int, int)? WebP(ReadOnlySpan<byte> data)
        {
            var kind = data[12..16];
            if (kind.SequenceEqual("VP8 "u8) && data.Length >= 30)
            {
                return Positive(BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF);
            }

            if (kind.SequenceEqual("VP8L"u8) && data.Length >= 25)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
                return Positive((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
            }

            if (kind.SequenceEqual("VP8X"u8) && data.Length >= 30)
            {
                return Positive((data[24] | data[25] << 8 | data[26] << 16) + 1, (data[27] | data[28] << 8 | data[29] << 16) + 1);
            }

            return null;
        }
    }
}
