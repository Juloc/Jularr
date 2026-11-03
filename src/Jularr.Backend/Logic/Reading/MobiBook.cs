using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Jularr.Backend.Logic.Reading
{
    // A Kindle book from its Palm database: an old PalmDOC text, a MOBI (AZW) with its HTML split at page breaks, or a KF8 (AZW3) put together from its skeletons
    // and fragments. Pictures come from the image records. Encrypted books raise BookProtectedException; HUFF/CDIC compression raises BookCompressionException.
    internal static partial class MobiBook
    {
        private const uint NullIndex = 0xFFFFFFFF;
        private const int MaximumIndexEntries = 100_000;

        // A text record decompresses to 4 KiB; more than this is no text record.
        private const int RecordBytes = 64 * 1024;

        // About this much HTML makes one block of a long chapter; the writer starts a new section between blocks.
        private const int BlockCharacters = 100_000;

        // Every pattern is bounded and gives up after a while; a timeout reads as a broken file.
        private const int PatternTimeout = 2000;

        [GeneratedRegex(@"filepos\s{0,16}=\s{0,16}[""']?0*(\d{1,10})[""']?", RegexOptions.IgnoreCase, PatternTimeout)]
        private static partial Regex FilePosition();

        [GeneratedRegex(@"<img\b[^>]{0,1024}?\brecindex\s{0,16}=\s{0,16}[""']?(\d{1,6})[""']?[^>]{0,1024}>", RegexOptions.IgnoreCase, PatternTimeout)]
        private static partial Regex RecordImage();

        [GeneratedRegex(@"<mbp:pagebreak[^>]{0,1024}>", RegexOptions.IgnoreCase, PatternTimeout)]
        private static partial Regex PageBreak();

        // Wrappers the reader's sanitizer would drop with their text.
        [GeneratedRegex(@"</?(font|center|big|small|mbp:[a-z]{1,32}|guide|reference|html|body)\b[^>]{0,1024}>", RegexOptions.IgnoreCase, PatternTimeout)]
        private static partial Regex Wrappers();

        [GeneratedRegex(@"src\s{0,16}=\s{0,16}[""']kindle:embed:([0-9A-Va-v]{1,8})(\?[^""']{0,256})?[""']", RegexOptions.IgnoreCase, PatternTimeout)]
        private static partial Regex EmbeddedImage();

        [GeneratedRegex(@"href\s{0,16}=\s{0,16}[""']kindle:pos:fid:([0-9A-Va-v]{1,8}):off:[0-9A-Va-v]{1,12}[""']", RegexOptions.IgnoreCase, PatternTimeout)]
        private static partial Regex PositionLink();

        [GeneratedRegex(@"<[^>]{0,1024}>", RegexOptions.None, PatternTimeout)]
        private static partial Regex Tags();

        [GeneratedRegex(@"\bid=""(fp\d{1,10})""", RegexOptions.None, PatternTimeout)]
        private static partial Regex PositionAnchor();

        private sealed record Header(int Compression, int TextRecords, int Encryption, int Encoding, int Version, uint FirstImage, int ExtraFlags, uint FlowTable, uint FlowCount,
            uint FragmentIndex, uint SkeletonIndex, string? Title, string? Language, uint? Boundary);

        public static ConvertedBook Read(byte[] data)
        {
            if (data.Length < 86)
            {
                throw new InvalidDataException("The file is no Palm database.");
            }

            var type = Encoding.ASCII.GetString(data, 60, 8);
            var records = Records(data);
            if (type == "TEXtREAd")
            {
                var palm = Read(data, records, 0, false);
                return TextBook.Read(Text(data, records, 0, palm));
            }

            if (type != "BOOKMOBI")
            {
                throw new InvalidDataException("The file is no MOBI.");
            }

            var header = Read(data, records, 0, true);
            if (header.Encryption != 0)
            {
                throw new BookProtectedException();
            }

            // A KF8 book alone, or the KF8 part behind the boundary of a combined file (whose pictures are shared and counted from the MOBI header's first); else the MOBI text.
            if (header.Version == 8)
            {
                return Kf8(data, records, 0, header, header);
            }

            if (header.Boundary is { } boundary && boundary > 1 && boundary < records.Length && Record(data, records, boundary - 1).StartsWith("BOUNDARY"u8))
            {
                var kf8 = Read(data, records, (int)boundary, true);
                if (kf8.Encryption != 0)
                {
                    throw new BookProtectedException();
                }

                return Kf8(data, records, (int)boundary, kf8, header);
            }

            return Mobi(data, records, header);
        }

        // Each record's start and end.
        private static (int Start, int End)[] Records(byte[] data)
        {
            var count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(76));
            if (count == 0 || 78 + count * 8L > data.Length)
            {
                throw new InvalidDataException("The record list is broken.");
            }

            var records = new (int Start, int End)[count];
            for (var index = 0; index < count; index++)
            {
                var start = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(78 + index * 8));
                var end = index + 1 < count ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(78 + (index + 1) * 8)) : (uint)data.Length;
                if (start > end || end > data.Length)
                {
                    throw new InvalidDataException("A record lies outside the file.");
                }

                records[index] = ((int)start, (int)end);
            }

            return records;
        }

        private static ReadOnlySpan<byte> Record(byte[] data, (int Start, int End)[] records, long index) =>
            index >= 0 && index < records.Length ? data.AsSpan(records[index].Start, records[index].End - records[index].Start) : throw new InvalidDataException("A record is missing.");

        // The PalmDOC header of a first record and, in a MOBI, its MOBI header and EXTH facts.
        private static Header Read(byte[] data, (int Start, int End)[] records, int first, bool mobi)
        {
            var record = Record(data, records, first);
            if (record.Length < 16)
            {
                throw new InvalidDataException("The first record is too short.");
            }

            var compression = BinaryPrimitives.ReadUInt16BigEndian(record);
            var textRecords = BinaryPrimitives.ReadUInt16BigEndian(record[8..]);
            if (!mobi)
            {
                return new Header(compression, textRecords, 0, 1252, 0, NullIndex, 0, NullIndex, 0, NullIndex, NullIndex, null, null, null);
            }

            var encryption = BinaryPrimitives.ReadUInt16BigEndian(record[12..]);
            if (record.Length < 24 || !record.Slice(16, 4).SequenceEqual("MOBI"u8))
            {
                return new Header(compression, textRecords, encryption, 1252, 0, NullIndex, 0, NullIndex, 0, NullIndex, NullIndex, null, null, null);
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(record[20..]);
            var encoding = (int)Field(record, length, 28);
            var version = (int)Field(record, length, 36);
            var extraFlags = length >= 0xE4 && record.Length >= 0xF4 ? BinaryPrimitives.ReadUInt16BigEndian(record[0xF2..]) : 0;

            // The full name, then the EXTH title, language and the KF8 boundary.
            var text = TextBook.CodePage(encoding is 65001 ? 65001 : 1252);
            var nameOffset = Field(record, length, 84);
            var nameLength = Field(record, length, 88);
            string? title = nameOffset != NullIndex && nameLength is > 0 and < 1024 && (long)nameOffset + nameLength <= record.Length ? text.GetString(record.Slice((int)nameOffset, (int)nameLength)) : null;
            string? language = null;
            uint? boundary = null;
            if ((Field(record, length, 128) & 0x40) != 0 && 16L + length + 12 <= record.Length && record.Slice((int)(16 + length), 4).SequenceEqual("EXTH"u8))
            {
                var exth = record[(int)(16 + length)..];
                var count = BinaryPrimitives.ReadUInt32BigEndian(exth[8..]);
                var position = 12;
                for (var index = 0; index < count && index < 10_000 && position + 8 <= exth.Length; index++)
                {
                    var kind = BinaryPrimitives.ReadUInt32BigEndian(exth[position..]);
                    var size = BinaryPrimitives.ReadUInt32BigEndian(exth[(position + 4)..]);
                    if (size < 8 || position + (long)size > exth.Length)
                    {
                        break;
                    }

                    var value = exth.Slice(position + 8, (int)size - 8);
                    if (kind == 503) title = text.GetString(value);
                    else if (kind == 524) language = text.GetString(value).Trim();
                    else if (kind == 121 && value.Length >= 4) boundary = BinaryPrimitives.ReadUInt32BigEndian(value);
                    position += (int)size;
                }
            }

            var flowCount = Field(record, length, 0xC4);
            return new Header(compression, textRecords, encryption, encoding, version, Field(record, length, 0x6C), extraFlags, flowCount is > 1 and not NullIndex ? Field(record, length, 0xC0) : NullIndex,
                flowCount, Field(record, length, 0xF8), Field(record, length, 0xFC), title?.Trim() is { Length: > 0 } named ? named : null, language is { Length: > 0 and <= 35 } ? language : null,
                boundary is NullIndex ? null : boundary);
        }

        // A number of the MOBI header; NullIndex where the header is too short for it.
        private static uint Field(ReadOnlySpan<byte> record, uint length, int offset) =>
            offset + 4 <= record.Length && offset + 4 <= 16 + length ? BinaryPrimitives.ReadUInt32BigEndian(record[offset..]) : NullIndex;

        // The decompressed text of a book's text records, their trailing entries removed.
        private static byte[] Text(byte[] data, (int Start, int End)[] records, int first, Header header)
        {
            if (header.Compression == 17480)
            {
                throw new BookCompressionException();
            }

            if (header.Compression is not (1 or 2))
            {
                throw new InvalidDataException("The text compression is unknown.");
            }

            using var output = new MemoryStream();
            var buffer = new byte[RecordBytes];
            for (var index = 1; index <= header.TextRecords; index++)
            {
                var record = Record(data, records, first + index);
                record = record[..Math.Max(0, record.Length - Trailing(record, header.ExtraFlags))];
                if (header.Compression == 1)
                {
                    output.Write(record);
                }
                else
                {
                    Decompress(record, output, buffer);
                }

                if (output.Length > BookConversion.MaximumTextBytes)
                {
                    throw new InvalidDataException("The book exceeds the size limit.");
                }
            }

            return output.ToArray();
        }

        // The bytes of extra data at a record's end: one size-tagged entry per flag bit, and the multibyte overlap.
        private static int Trailing(ReadOnlySpan<byte> record, int flags)
        {
            var size = 0;
            for (var bits = flags >> 1; bits != 0; bits >>= 1)
            {
                if ((bits & 1) == 0) continue;
                var value = 0;
                for (int shift = 0, position = record.Length - size - 1; position >= 0 && shift < 28; shift += 7, position--)
                {
                    var current = record[position];
                    value |= (current & 0x7F) << shift;
                    if ((current & 0x80) != 0) break;
                }

                size += value;
                if (size > record.Length) return record.Length;
            }

            if ((flags & 1) != 0 && record.Length - size - 1 >= 0)
            {
                size += (record[record.Length - size - 1] & 0x3) + 1;
            }

            return Math.Min(size, record.Length);
        }

        // PalmDOC LZ77: literals, runs of literals, a space with a character, and copies from the last 2047 bytes of this record. A record holds 4 KiB of text;
        // one that decompresses beyond the shared buffer is broken.
        private static void Decompress(ReadOnlySpan<byte> input, MemoryStream output, byte[] buffer)
        {
            var length = 0;
            for (var index = 0; index < input.Length;)
            {
                var current = input[index++];
                if (length + 10 > buffer.Length)
                {
                    throw new InvalidDataException("A text record decompresses too far.");
                }

                if (current is 0 or >= 9 and <= 0x7F)
                {
                    buffer[length++] = current;
                }
                else if (current <= 8)
                {
                    if (index + current > input.Length) throw new InvalidDataException("A text record ends early.");
                    input.Slice(index, current).CopyTo(buffer.AsSpan(length));
                    length += current;
                    index += current;
                }
                else if (current >= 0xC0)
                {
                    buffer[length++] = (byte)' ';
                    buffer[length++] = (byte)(current ^ 0x80);
                }
                else
                {
                    if (index >= input.Length) throw new InvalidDataException("A text record ends early.");
                    var pair = (current << 8) | input[index++];
                    var distance = (pair >> 3) & 0x7FF;
                    var count = (pair & 7) + 3;
                    if (distance == 0 || distance > length) throw new InvalidDataException("A text record refers outside itself.");
                    for (var copy = 0; copy < count; copy++)
                    {
                        buffer[length] = buffer[length - distance];
                        length++;
                    }
                }
            }

            output.Write(buffer, 0, length);
        }

        // MOBI HTML: positions named by links become anchors, image records become pictures, page breaks start chapters.
        private static ConvertedBook Mobi(byte[] data, (int Start, int End)[] records, Header header)
        {
            var book = new ConvertedBook { Title = header.Title, Language = header.Language };
            var raw = Encoding.Latin1.GetString(Text(data, records, 0, header));

            // Anchors at the byte positions links name, before the tag they would split (one pass over the text in position order); then the links name them.
            var positions = FilePosition().Matches(raw).Select(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Where(position => position < raw.Length)
                .Distinct().Order().Take(MaximumIndexEntries).ToArray();
            var html = new StringBuilder(raw.Length + positions.Length * 24);
            var copied = 0;
            var open = -1;
            var scanned = 0;
            foreach (var position in positions)
            {
                // The last tag opened before the position, unless it closed again.
                for (; scanned < position; scanned++)
                {
                    if (raw[scanned] == '<') open = scanned;
                    else if (raw[scanned] == '>') open = -1;
                }

                var at = Math.Max(copied, open >= 0 ? open : (int)position);
                html.Append(raw, copied, at - copied).Append("<a id=\"fp").Append(position.ToString(CultureInfo.InvariantCulture)).Append("\"></a>");
                copied = at;
            }

            html.Append(raw, copied, raw.Length - copied);

            raw = FilePosition().Replace(html.ToString(), match => "href=\"#fp" + long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "\"");
            var text = TextBook.CodePage(header.Encoding is 65001 ? 65001 : 1252).GetString(Encoding.Latin1.GetBytes(raw));
            text = RecordImage().Replace(text, match =>
            {
                var number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                return Picture(book, data, records, [header.FirstImage + number - 1L]) is { } path ? "<img src=\"" + path + "\" alt=\"\"/>" : "";
            });

            foreach (var chapter in PageBreak().Split(text).Select(Clean))
            {
                Add(book, chapter, null);
            }

            return book;
        }

        // KF8: the text flow's skeletons with their fragments inserted are the book's files; links to a fragment lead to its file.
        private static ConvertedBook Kf8(byte[] data, (int Start, int End)[] records, int first, Header header, Header outer)
        {
            var book = new ConvertedBook { Title = header.Title ?? outer.Title, Language = header.Language ?? outer.Language };
            var raw = Text(data, records, first, header);

            // The first flow is the text; the others hold styles and pictures the reader does not use.
            var flow = raw.Length;
            if (header.FlowTable != NullIndex)
            {
                var table = Record(data, records, first + header.FlowTable);
                if (table.Length < 12 || !table[..4].SequenceEqual("FDST"u8))
                {
                    throw new InvalidDataException("The flow table is broken.");
                }

                var start = BinaryPrimitives.ReadUInt32BigEndian(table[4..]);
                if (start + 8L <= table.Length)
                {
                    flow = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(table[((int)start + 4)..]), (uint)raw.Length);
                }
            }

            var text = Encoding.Latin1.GetString(raw, 0, flow);
            var parts = new List<string>();
            var fragmentParts = new List<int>();
            if (header.SkeletonIndex != NullIndex && header.FragmentIndex != NullIndex)
            {
                var skeletons = Index(data, records, first + header.SkeletonIndex);
                var fragments = Index(data, records, first + header.FragmentIndex);
                // Skeletons follow each other, each with its fragments behind it; the files together stay within the text limit.
                var next = 0;
                var end = 0L;
                var total = 0L;
                foreach (var (_, tags) in skeletons)
                {
                    if (!tags.TryGetValue(1, out var count) || count.Count < 1 || !tags.TryGetValue(6, out var place) || place.Count < 2 || place[0] < end || place[0] + place[1] > text.Length)
                    {
                        throw new InvalidDataException("A skeleton is broken.");
                    }

                    var skeleton = new StringBuilder(text, (int)place[0], (int)place[1], (int)place[1]);
                    var position = place[0] + place[1];
                    var moved = 0L;
                    for (var index = 0; index < count[0]; index++, next++)
                    {
                        if (next >= fragments.Count || !int.TryParse(fragments[next].Name, NumberStyles.None, CultureInfo.InvariantCulture, out var insert)
                            || !fragments[next].Tags.TryGetValue(6, out var piece) || piece.Count < 2 || position + piece[1] > text.Length)
                        {
                            throw new InvalidDataException("A fragment is broken.");
                        }

                        // Fragments go in where the skeleton says, usually at its end; text moved for an earlier place is bounded too.
                        var at = (int)Math.Clamp(insert - place[0], 0, skeleton.Length);
                        moved += skeleton.Length - at;
                        total += piece[1];
                        if (moved > BookConversion.MaximumTextBytes || total + skeleton.Length > BookConversion.MaximumTextBytes)
                        {
                            throw new InvalidDataException("The book exceeds the size limit.");
                        }

                        skeleton.Insert(at, text.AsSpan((int)position, (int)piece[1]));
                        position += piece[1];
                        fragmentParts.Add(parts.Count);
                    }

                    total += place[1];
                    end = position;
                    parts.Add(skeleton.ToString());
                }
            }
            else
            {
                parts.Add(text);
            }

            var encoding = TextBook.CodePage(header.Encoding is 65001 ? 65001 : 1252);
            for (var index = 0; index < parts.Count; index++)
            {
                var part = encoding.GetString(Encoding.Latin1.GetBytes(parts[index]));
                part = Inside(part, "body") ?? part;
                part = EmbeddedImage().Replace(part, match =>
                {
                    var number = Convert32(match.Groups[1].Value);
                    var path = Picture(book, data, records, [outer.FirstImage + number - 1L, first + header.FirstImage + number - 1L, header.FirstImage + number - 1L]);
                    return path is null ? "" : "src=\"" + path + "\"";
                });
                part = PositionLink().Replace(part, match => Convert32(match.Groups[1].Value) is var fragment && fragment < fragmentParts.Count
                    ? "href=\"#kf8-part-" + fragmentParts[(int)fragment].ToString(CultureInfo.InvariantCulture) + "\""
                    : "");
                Add(book, Clean(part), "kf8-part-" + index.ToString(CultureInfo.InvariantCulture));
            }

            return book;
        }

        // The entries of an INDX index in order: each entry's name and its tag values.
        private static List<(string Name, Dictionary<int, List<long>> Tags)> Index(byte[] data, (int Start, int End)[] records, long first)
        {
            var main = Record(data, records, first);
            if (main.Length < 28 || !main[..4].SequenceEqual("INDX"u8))
            {
                throw new InvalidDataException("An index is broken.");
            }

            var headerLength = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(main[4..]), int.MaxValue - 16);
            var count = BinaryPrimitives.ReadUInt32BigEndian(main[24..]);
            if (headerLength + 12L > main.Length || !main.Slice(headerLength, 4).SequenceEqual("TAGX"u8) || count > 10_000)
            {
                throw new InvalidDataException("An index has no tag table.");
            }

            // The tag table: tag, values per entry, mask and end flag.
            var tagx = main[headerLength..];
            var tagxLength = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(tagx[4..]), (uint)tagx.Length);
            var controlBytes = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(tagx[8..]), 16);
            var tags = new List<(int Tag, int Values, int Mask, bool End)>();
            for (var position = 12; position + 4 <= tagxLength; position += 4)
            {
                tags.Add((tagx[position], tagx[position + 1], tagx[position + 2], tagx[position + 3] == 1));
            }

            var entries = new List<(string, Dictionary<int, List<long>>)>();
            for (var index = 1; index <= count; index++)
            {
                var record = Record(data, records, first + index);
                if (record.Length < 28 || !record[..4].SequenceEqual("INDX"u8))
                {
                    throw new InvalidDataException("An index record is broken.");
                }

                var idxt = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(record[20..]), int.MaxValue - 16);
                var entryCount = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(record[24..]), MaximumIndexEntries + 1);
                if (idxt + 4L + entryCount * 2L > record.Length || entries.Count + entryCount > MaximumIndexEntries)
                {
                    throw new InvalidDataException("An index record is broken.");
                }

                for (var entry = 0; entry < entryCount; entry++)
                {
                    var start = BinaryPrimitives.ReadUInt16BigEndian(record[(idxt + 4 + entry * 2)..]);
                    var end = entry + 1 < entryCount ? BinaryPrimitives.ReadUInt16BigEndian(record[(idxt + 4 + (entry + 1) * 2)..]) : idxt;
                    if (start >= end || end > record.Length || 1 + record[start] > end - start)
                    {
                        throw new InvalidDataException("An index entry is broken.");
                    }

                    var name = Encoding.ASCII.GetString(record.Slice(start + 1, record[start]));
                    entries.Add((name, TagValues(record.Slice(start + 1 + record[start], end - start - 1 - record[start]), controlBytes, tags)));
                }
            }

            return entries;
        }

        // The tag values of an index entry from its control bytes and variable-width numbers.
        private static Dictionary<int, List<long>> TagValues(ReadOnlySpan<byte> entry, int controlBytes, List<(int Tag, int Values, int Mask, bool End)> tags)
        {
            if (controlBytes > entry.Length)
            {
                throw new InvalidDataException("An index entry is broken.");
            }

            var control = entry[..controlBytes];
            var position = controlBytes;
            var controlIndex = 0;
            var present = new List<(int Tag, int? Count, int? Bytes, int Values)>();
            foreach (var tag in tags)
            {
                if (tag.End)
                {
                    controlIndex++;
                    continue;
                }

                if (controlIndex >= control.Length || tag.Mask == 0)
                {
                    break;
                }

                var value = control[controlIndex] & tag.Mask;
                if (value == 0)
                {
                    continue;
                }

                if (value == tag.Mask && BitOperationsCount(tag.Mask) > 1)
                {
                    present.Add((tag.Tag, null, (int)Math.Min(Number(entry, ref position), entry.Length), tag.Values));
                }
                else
                {
                    var mask = tag.Mask;
                    while ((mask & 1) == 0)
                    {
                        mask >>= 1;
                        value >>= 1;
                    }

                    present.Add((tag.Tag, value, null, tag.Values));
                }
            }

            var result = new Dictionary<int, List<long>>();
            foreach (var tag in present)
            {
                var values = new List<long>();
                if (tag.Count is { } count)
                {
                    for (var index = 0; index < count * tag.Values && values.Count < 1024; index++) values.Add(Number(entry, ref position));
                }
                else
                {
                    var end = position + tag.Bytes!.Value;
                    while (position < end && position < entry.Length && values.Count < 1024) values.Add(Number(entry, ref position));
                }

                result[tag.Tag] = values;
            }

            return result;
        }

        private static int BitOperationsCount(int value) => System.Numerics.BitOperations.PopCount((uint)value);

        // A forward variable-width number: seven bits a byte, the last byte marked by its high bit.
        private static long Number(ReadOnlySpan<byte> data, ref int position)
        {
            long value = 0;
            for (var count = 0; count < 5; count++)
            {
                if (position >= data.Length) throw new InvalidDataException("An index entry ends early.");
                var current = data[position++];
                value = (value << 7) | (uint)(current & 0x7F);
                if ((current & 0x80) != 0) return value;
            }

            throw new InvalidDataException("An index number is too long.");
        }

        // Kindle's base-32 numbers (0-9, A-V).
        private static long Convert32(string text)
        {
            long value = 0;
            foreach (var character in text.ToUpperInvariant())
            {
                value = value * 32 + (character <= '9' ? character - '0' : character - 'A' + 10);
            }

            return value;
        }

        // The first candidate record that is a picture.
        private static string? Picture(ConvertedBook book, byte[] data, (int Start, int End)[] records, long[] candidates)
        {
            foreach (var index in candidates.Distinct())
            {
                if (index > 0 && index < records.Length && Record(data, records, index) is { Length: > 8 } record && ComicPages.ContentType(record) is not null)
                {
                    var found = index;
                    return book.Image("r" + index.ToString(CultureInfo.InvariantCulture), () => Record(data, records, found).ToArray());
                }
            }

            return null;
        }

        private static string Clean(string html)
        {
            // The head goes; then the wrappers whose text would be lost.
            var head = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
            while (head >= 0 && head + 5 < html.Length && html[head + 5] is not ('>' or ' ' or '\t' or '\r' or '\n'))
            {
                head = html.IndexOf("<head", head + 5, StringComparison.OrdinalIgnoreCase);
            }

            var close = head < 0 ? -1 : html.IndexOf("</head>", head, StringComparison.OrdinalIgnoreCase);
            if (close >= 0)
            {
                html = html[..head] + html[(close + "</head>".Length)..];
            }

            return Wrappers().Replace(html, "");
        }

        // What an element of this name holds: from the end of its first opening tag to its closing tag or the end; null without it.
        private static string? Inside(string html, string name)
        {
            var open = html.IndexOf("<" + name, StringComparison.OrdinalIgnoreCase);
            var start = open < 0 ? -1 : html.IndexOf('>', open);
            if (start < 0)
            {
                return null;
            }

            var end = html.IndexOf("</" + name + ">", start, StringComparison.OrdinalIgnoreCase);
            return html[(start + 1)..(end < 0 ? html.Length : end)];
        }

        // The first heading of level 1 to 3 that closes, found in one pass: its level and its HTML.
        private static (int Level, string Html)? FirstHeading(string html)
        {
            var unclosed = new bool[4];
            var tagEnd = -1;
            for (var at = html.IndexOf("<h", StringComparison.OrdinalIgnoreCase); at >= 0 && at + 3 < html.Length; at = html.IndexOf("<h", at + 2, StringComparison.OrdinalIgnoreCase))
            {
                var level = html[at + 2] - '0';
                if (level is < 1 or > 3 || char.IsAsciiLetterOrDigit(html[at + 3]) || html[at + 3] == '_' || unclosed[level])
                {
                    continue;
                }

                if (tagEnd < at && (tagEnd = html.IndexOf('>', at)) < 0)
                {
                    return null;
                }

                var close = html.IndexOf("</h" + (char)('0' + level) + ">", tagEnd, StringComparison.OrdinalIgnoreCase);
                if (close < 0)
                {
                    unclosed[level] = true;
                    continue;
                }

                return (level, html[(tagEnd + 1)..close]);
            }

            return null;
        }

        // Text outside the tags, found in one pass.
        private static bool HasText(string html)
        {
            var inTag = false;
            foreach (var character in html)
            {
                if (character == '<') inTag = true;
                else if (character == '>') inTag = false;
                else if (!inTag && !char.IsWhiteSpace(character)) return true;
            }

            return false;
        }

        // A chapter's HTML: its first heading names it, a long one is cut before paragraphs into blocks, its position anchors are registered.
        private static void Add(ConvertedBook book, string html, string? anchor)
        {
            if (!HasText(html) && !html.Contains("<img", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var heading = FirstHeading(html);
            var title = heading is { } found ? string.Join(' ', WebUtility.HtmlDecode(Tags().Replace(found.Html.Length > 2000 ? found.Html[..2000] : found.Html, " "))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : "";
            var chapter = book.Chapter(title.Length is > 0 and <= 300 ? title : null, heading is { Level: > 1 } ? 1 : 0);
            if (anchor is not null)
            {
                book.Anchors.TryAdd(anchor, (book.Chapters.Count - 1, 0));
            }

            var start = 0;
            while (start < html.Length)
            {
                var end = html.Length;
                if (html.Length - start > BlockCharacters)
                {
                    var cut = html.IndexOf("<p", start + BlockCharacters, StringComparison.OrdinalIgnoreCase);
                    end = cut > 0 ? cut : html.Length;
                }

                var block = html[start..end];
                foreach (Match match in PositionAnchor().Matches(block))
                {
                    book.Anchors.TryAdd(match.Groups[1].Value, (book.Chapters.Count - 1, chapter.Blocks.Count));
                }

                book.Add(block);
                start = end;
            }
        }
    }
}
