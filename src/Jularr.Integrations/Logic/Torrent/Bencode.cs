using System.Globalization;
using System.Text;

namespace Jularr.Integrations.Logic.Torrent
{
    // Bounded bencode: integers are long, strings byte[], lists List<object>, dictionaries Dictionary<string, object>.
    // A dictionary also remembers where each value lay, so the info dictionary is hashed from its exact bytes.
    internal static class Bencode
    {
        private const int MaximumDepth = 32;

        public sealed class Dictionary : Dictionary<string, object>
        {
            public Dictionary<string, Range> Spans { get; } = new(StringComparer.Ordinal);
        }

        public static object Decode(ReadOnlySpan<byte> data)
        {
            var position = 0;
            var value = Read(data, ref position, 0);
            if (position != data.Length) throw new InvalidDataException("Trailing bencode data.");
            return value;
        }

        // The value and how many bytes it took, for a dictionary followed by raw data (a metadata piece).
        public static object DecodePrefix(ReadOnlySpan<byte> data, out int length)
        {
            length = 0;
            return Read(data, ref length, 0);
        }

        public static byte[] Encode(object value)
        {
            using var output = new MemoryStream();
            Write(output, value);
            return output.ToArray();
        }

        public static string Text(object? value) => value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : throw new InvalidDataException("Expected a bencode string.");

        private static object Read(ReadOnlySpan<byte> data, ref int position, int depth)
        {
            if (depth > MaximumDepth || position >= data.Length) throw new InvalidDataException("Invalid bencode data.");
            switch (data[position])
            {
                case (byte)'i':
                {
                    var end = data[position..].IndexOf((byte)'e');
                    if (end < 2) throw new InvalidDataException("Invalid bencode integer.");
                    var text = Encoding.ASCII.GetString(data.Slice(position + 1, end - 1));
                    if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                        || text.Length > 1 && (text[0] == '0' || text.StartsWith("-0", StringComparison.Ordinal)))
                    {
                        throw new InvalidDataException("Invalid bencode integer.");
                    }

                    position += end + 1;
                    return number;
                }
                case (byte)'l':
                {
                    position++;
                    var list = new List<object>();
                    while (position < data.Length && data[position] != (byte)'e') list.Add(Read(data, ref position, depth + 1));
                    if (position >= data.Length) throw new InvalidDataException("Unterminated bencode list.");
                    position++;
                    return list;
                }
                case (byte)'d':
                {
                    position++;
                    var dictionary = new Dictionary();
                    while (position < data.Length && data[position] != (byte)'e')
                    {
                        var key = Encoding.UTF8.GetString(ReadString(data, ref position));
                        var start = position;
                        var value = Read(data, ref position, depth + 1);
                        dictionary[key] = value;
                        dictionary.Spans[key] = new Range(start, position);
                    }

                    if (position >= data.Length) throw new InvalidDataException("Unterminated bencode dictionary.");
                    position++;
                    return dictionary;
                }
                default:
                    return ReadString(data, ref position);
            }
        }

        private static byte[] ReadString(ReadOnlySpan<byte> data, ref int position)
        {
            var colon = data[position..].IndexOf((byte)':');
            if (colon < 1 || colon > 10) throw new InvalidDataException("Invalid bencode string.");
            if (!int.TryParse(Encoding.ASCII.GetString(data.Slice(position, colon)), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || length > data.Length - position - colon - 1)
            {
                throw new InvalidDataException("Invalid bencode string length.");
            }

            var value = data.Slice(position + colon + 1, length).ToArray();
            position += colon + 1 + length;
            return value;
        }

        private static void Write(Stream output, object value)
        {
            switch (value)
            {
                case long number:
                    output.Write(Encoding.ASCII.GetBytes("i" + number.ToString(CultureInfo.InvariantCulture) + "e"));
                    break;
                case int number:
                    Write(output, (long)number);
                    break;
                case string text:
                    Write(output, Encoding.UTF8.GetBytes(text));
                    break;
                case byte[] bytes:
                    output.Write(Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":"));
                    output.Write(bytes);
                    break;
                case IDictionary<string, object> dictionary:
                    output.WriteByte((byte)'d');
                    foreach (var (key, item) in dictionary.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                    {
                        Write(output, key);
                        Write(output, item);
                    }

                    output.WriteByte((byte)'e');
                    break;
                case System.Collections.IEnumerable list:
                    output.WriteByte((byte)'l');
                    foreach (var item in list) Write(output, item);
                    output.WriteByte((byte)'e');
                    break;
                default:
                    throw new ArgumentException("Unsupported bencode value.", nameof(value));
            }
        }
    }
}
