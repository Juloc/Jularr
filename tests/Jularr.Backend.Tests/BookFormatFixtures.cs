using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Jularr.Backend.Tests
{
    // Small books of every converted format, built in code: FB2, TXT, RTF, DOCX and the Kindle formats (PalmDOC, MOBI, KF8).
    internal static class BookFormatFixtures
    {
        public static readonly byte[] Png = ReadingTests.Picture(4, 3);

        public static byte[] Fb2(string title = "Night Train") => Encoding.UTF8.GetBytes($$"""
            <?xml version="1.0" encoding="utf-8"?>
            <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0" xmlns:l="http://www.w3.org/1999/xlink">
              <description><title-info><book-title>{{title}}</book-title><lang>en</lang><coverpage><image l:href="#cover.png"/></coverpage></title-info></description>
              <body>
                <title><p>{{title}}</p></title>
                <section><title><p>Chapter One</p></title>
                  <p>The night train left at nine.</p>
                  <p>She read <emphasis>slowly</emphasis> and <strong>carefully</strong>.<a l:href="#n1" type="note">1</a></p>
                  <poem><stanza><v>A line of verse</v><v>And another</v></stanza></poem>
                </section>
                <section><title><p>Chapter Two</p></title>
                  <section><title><p>A Part</p></title><p>The second chapter ends.</p></section>
                </section>
              </body>
              <body name="notes"><section id="n1"><title><p>1</p></title><p>A note about the train.</p></section></body>
              <binary id="cover.png" content-type="image/png">{{Convert.ToBase64String(Png)}}</binary>
            </FictionBook>
            """);

        public static byte[] Zip(params (string Name, byte[] Content)[] entries)
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, content) in entries)
                {
                    using var entry = archive.CreateEntry(name).Open();
                    entry.Write(content);
                }
            }

            return stream.ToArray();
        }

        public static byte[] Text() => Encoding.UTF8.GetBytes("""
            A Short Book

            Chapter 1

            The night train
            left at nine.

            Chapter 2: Arrival

            Grüße from the station.
            """);

        public static byte[] Rtf() => Encoding.ASCII.GetBytes(
            @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Times;}}{\stylesheet{\s0 Normal;}{\s1\b heading 1;}}{\info{\title Rich Book}{\author Somebody}}" +
            @"{\*\generator Hidden Writer;}" +
            @"\pard\s1 Chapter One\par" +
            @"\pard The night train left at nine. {\b Bold words} and {\i slanted} caf\'e9 na\u239?ve.\par" +
            @"\pard {\*\shppict{\pict\pngblip\picw4\pich3 " + Convert.ToHexString(Png) + @"}}{\nonshppict{\pict\wmetafile8 0102}}\par" +
            @"{\header Running header\par}" +
            @"\pard\outlinelevel0 Chapter Two\par\pard The end.\par}");

        public static byte[] Docx(bool withDocument = true)
        {
            const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var document = $$"""
                <w:document xmlns:w="{{W}}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                  xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><w:body>
                  <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Chapter One</w:t></w:r></w:p>
                  <w:p><w:r><w:t xml:space="preserve">The night train left at nine. </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>Bold words</w:t></w:r><w:r><w:rPr><w:i w:val="0"/></w:rPr><w:t> plain</w:t></w:r></w:p>
                  <w:p><w:r><w:drawing><wp:inline><a:graphic><a:graphicData><pic:pic><pic:blipFill><a:blip r:embed="rId5"/></pic:blipFill></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
                  <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Cell A</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Cell B</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
                  <w:p><w:pPr><w:pStyle w:val="Heading2"/></w:pPr><w:r><w:t>A Part</w:t></w:r></w:p>
                  <w:p><w:r><w:t>The end.</w:t></w:r></w:p>
                </w:body></w:document>
                """;
            var styles = $$"""
                <w:styles xmlns:w="{{W}}"><w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/></w:style>
                <w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/></w:style></w:styles>
                """;
            const string Links = """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId5" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image1.png"/>
                  <Relationship Id="rId9" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://example.test" TargetMode="External"/>
                </Relationships>
                """;
            const string Core = """<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Word Book</dc:title></cp:coreProperties>""";
            (string, byte[])[] entries =
            [
                ("[Content_Types].xml", Encoding.UTF8.GetBytes("<Types/>")),
                ("word/styles.xml", Encoding.UTF8.GetBytes(styles)),
                ("word/_rels/document.xml.rels", Encoding.UTF8.GetBytes(Links)),
                ("word/media/image1.png", Png),
                ("docProps/core.xml", Encoding.UTF8.GetBytes(Core))
            ];
            return Zip(withDocument ? [("word/document.xml", Encoding.UTF8.GetBytes(document)), .. entries] : entries);
        }

        // An old PalmDOC (TEXtREAd) with compressed plain text.
        public static byte[] PalmDoc(string text)
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            var records = Chunks(bytes).Select(Compress).ToList();
            var first = new byte[16];
            BinaryPrimitives.WriteUInt16BigEndian(first, 2);
            BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(4), (uint)bytes.Length);
            BinaryPrimitives.WriteUInt16BigEndian(first.AsSpan(8), (ushort)records.Count);
            BinaryPrimitives.WriteUInt16BigEndian(first.AsSpan(10), 4096);
            return Palm("TEXtREAd", [first, .. records]);
        }

        // A MOBI 6 book: HTML text in compressed records with trailing entries, an image record, an EXTH title and language.
        public static byte[] Mobi(string html, int encryption = 0, int compression = 2, bool image = true)
        {
            var text = Encoding.UTF8.GetBytes(html);
            var records = Chunks(text).Select(chunk => (byte[])[.. compression == 2 ? Compress(chunk) : chunk, 0x00, 0xAA, 0x82]).ToList();
            var first = Header(compression, text.Length, records.Count, encryption, 6, (uint)(records.Count + 1), 0b11, null, "Kindle Book");
            return Palm("BOOKMOBI", [first, .. records, .. image ? new[] { Png } : []]);
        }

        // A KF8 (AZW3) book: two files, each a skeleton with one fragment, a picture by kindle:embed, a flow table and the skeleton and fragment indexes.
        public static byte[] Kf8(bool overlapping = false) => Palm("BOOKMOBI", Kf8Records(true, overlapping));

        // A combined file: a MOBI 6 part with the picture, the boundary, then a KF8 part that shares the MOBI part's picture.
        public static byte[] Joint()
        {
            var text = Encoding.UTF8.GetBytes("<p>The old MOBI text.</p>");
            var records = Chunks(text).Select(Compress).ToList();
            var first = Header(2, text.Length, records.Count, 0, 6, (uint)(records.Count + 1), 0, null, "Joint Book", (uint)(records.Count + 3));
            return Palm("BOOKMOBI", [first, .. records, Png, "BOUNDARY"u8.ToArray(), .. Kf8Records(false, false)]);
        }

        private static List<byte[]> Kf8Records(bool image, bool overlapping)
        {
            const string FirstSkeleton = "<html><head><title>x</title></head><body aid=\"0\"></body></html>";
            const string SecondSkeleton = "<html><head></head><body aid=\"1\"></body></html>";
            const string FirstFragment = "<h1>Chapter One</h1><p>The night train left at nine.</p><p><img src=\"kindle:embed:0001?mime=image/png\"/></p><p><a href=\"kindle:pos:fid:0001:off:0000000000\">On</a></p>";
            const string SecondFragment = "<h2>Chapter Two</h2><p>The second file ends.</p>";
            var flow = FirstSkeleton + FirstFragment + SecondSkeleton + SecondFragment;
            const string Css = "p { margin: 0 }";
            var text = Encoding.UTF8.GetBytes(flow + Css);
            var records = Chunks(text).Select(Compress).ToList();

            // Records: header, text, picture (unless shared), flow table, skeleton index (2), fragment index (2).
            var fdstIndex = (uint)records.Count + (image ? 2u : 1u);
            var first = Header(2, text.Length, records.Count, 0, 8, image ? (uint)records.Count + 1 : 0xFFFFFFFF, 0, (Fdst: fdstIndex, FdstCount: 2u, Skeleton: fdstIndex + 1, Fragment: fdstIndex + 3), "Kindle Eight");
            var fdst = new byte[28];
            "FDST"u8.CopyTo(fdst);
            BinaryPrimitives.WriteUInt32BigEndian(fdst.AsSpan(4), 12);
            BinaryPrimitives.WriteUInt32BigEndian(fdst.AsSpan(8), 2);
            BinaryPrimitives.WriteUInt32BigEndian(fdst.AsSpan(12), 0);
            BinaryPrimitives.WriteUInt32BigEndian(fdst.AsSpan(16), (uint)flow.Length);
            BinaryPrimitives.WriteUInt32BigEndian(fdst.AsSpan(20), (uint)flow.Length);
            BinaryPrimitives.WriteUInt32BigEndian(fdst.AsSpan(24), (uint)text.Length);

            // Skeleton entries: tag 1 (fragment count), tag 6 (start, length); fragment entries named by their insert position: tags 2, 3, 4 and 6 (start, length).
            var skeletonTags = new byte[][] { [1, 1, 0x03, 0], [6, 2, 0x0C, 0], [0, 0, 0, 1] };
            var secondStart = FirstSkeleton.Length + FirstFragment.Length;
            var skeletons = new[]
            {
                Entry("SKEL0000000", 0x05, 1, 0, FirstSkeleton.Length),
                Entry("SKEL0000001", 0x05, 1, overlapping ? 0 : secondStart, SecondSkeleton.Length)
            };
            var fragmentTags = new byte[][] { [2, 1, 0x01, 0], [3, 1, 0x02, 0], [4, 1, 0x04, 0], [6, 2, 0x08, 0], [0, 0, 0, 1] };
            var fragments = new[]
            {
                Entry(FirstSkeleton.IndexOf("</body>", StringComparison.Ordinal).ToString("D10"), 0x0F, 0, 0, 0, 0, FirstFragment.Length),
                Entry((secondStart + SecondSkeleton.IndexOf("</body>", StringComparison.Ordinal)).ToString("D10"), 0x0F, 0, 1, 1, 0, SecondFragment.Length)
            };
            return [first, .. records, .. image ? new[] { Png } : [], fdst, IndexHeader(skeletonTags), IndexRecord(skeletons), IndexHeader(fragmentTags), IndexRecord(fragments)];
        }

        // A MOBI 6 book whose EXTH record claims almost 2 GiB.
        public static byte[] HugeExth()
        {
            var book = Mobi("<p>The night train left at nine.</p>");
            var first = (int)BinaryPrimitives.ReadUInt32BigEndian(book.AsSpan(78));
            BinaryPrimitives.WriteUInt32BigEndian(book.AsSpan(first + 16 + 0x108 + 12 + 4), 0x7FFFFFFF);
            return book;
        }

        // A MOBI first record whose header says the text is encrypted, with nothing behind it.
        public static byte[] Protected() => Palm("BOOKMOBI", [Header(2, 10, 1, 2, 6, 2, 0, null, "Locked"), new byte[10]]);

        // The PalmDOC and MOBI header of the first record, with an EXTH title and language.
        private static byte[] Header(int compression, int textLength, int textRecords, int encryption, int version, uint firstImage, int extraFlags,
            (uint Fdst, uint FdstCount, uint Skeleton, uint Fragment)? kf8, string title, uint? boundary = null)
        {
            const int Length = 0x108;
            var name = Encoding.UTF8.GetBytes(title);
            var exth = new List<byte>("EXTH"u8.ToArray());
            var exthRecords = new List<byte[]> { ExthRecord(503, Encoding.UTF8.GetBytes(title)), ExthRecord(524, "en"u8.ToArray()) };
            if (boundary is { } kf8Header) exthRecords.Add(ExthRecord(121, BigEndian(kf8Header)));
            var exthLength = 12 + exthRecords.Sum(record => record.Length);
            exth.AddRange(BigEndian((uint)exthLength));
            exth.AddRange(BigEndian((uint)exthRecords.Count));
            exthRecords.ForEach(exth.AddRange);
            var record = new byte[16 + Length + exth.Count + name.Length + 4];
            BinaryPrimitives.WriteUInt16BigEndian(record, (ushort)compression);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(4), (uint)textLength);
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(8), (ushort)textRecords);
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(10), 4096);
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(12), (ushort)encryption);
            "MOBI"u8.CopyTo(record.AsSpan(16));
            for (var offset = 24; offset + 4 <= 16 + Length; offset += 4)
            {
                BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(offset), 0xFFFFFFFF);
            }

            void Set(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(offset), value);
            Set(20, Length);
            Set(24, 2);
            Set(28, 65001);
            Set(36, (uint)version);
            Set(84, (uint)(16 + Length + exth.Count));
            Set(88, (uint)name.Length);
            Set(0x6C, firstImage);
            Set(0x80, 0x40);
            Set(0xC4, kf8?.FdstCount ?? 0);
            if (kf8 is { } eight)
            {
                Set(0xC0, eight.Fdst);
                Set(0xF8, eight.Fragment);
                Set(0xFC, eight.Skeleton);
            }

            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(0xF2), (ushort)extraFlags);
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(0xF0), 0);
            exth.ToArray().CopyTo(record, 16 + Length);
            name.CopyTo(record, 16 + Length + exth.Count);
            return record;
        }

        private static byte[] ExthRecord(uint type, byte[] value) => [.. BigEndian(type), .. BigEndian((uint)(value.Length + 8)), .. value];

        private static byte[] BigEndian(uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            return bytes;
        }

        // A Palm database: its 78-byte header, the record list and the records.
        private static byte[] Palm(string type, IReadOnlyList<byte[]> records)
        {
            var header = new byte[78 + records.Count * 8 + 2];
            Encoding.ASCII.GetBytes("Fixture").CopyTo(header, 0);
            Encoding.ASCII.GetBytes(type).CopyTo(header, 60);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(76), (ushort)records.Count);
            var offset = header.Length;
            for (var index = 0; index < records.Count; index++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(78 + index * 8), (uint)offset);
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(82 + index * 8), (uint)index * 2);
                offset += records[index].Length;
            }

            return [.. header, .. records.SelectMany(record => record)];
        }

        private static IEnumerable<byte[]> Chunks(byte[] text)
        {
            for (var start = 0; start < text.Length; start += 4096)
            {
                yield return text[start..Math.Min(text.Length, start + 4096)];
            }
        }

        // PalmDOC LZ77 as Kindle writes it: copies of earlier bytes, a space joined to the next character, literals and runs of literals.
        private static byte[] Compress(byte[] input)
        {
            var output = new List<byte>();
            var index = 0;
            while (index < input.Length)
            {
                var bestLength = 0;
                var bestDistance = 0;
                for (var distance = 1; distance <= Math.Min(2047, index); distance++)
                {
                    var length = 0;
                    while (length < 10 && index + length < input.Length && input[index + length - distance] == input[index + length]) length++;
                    if (length > bestLength)
                    {
                        bestLength = length;
                        bestDistance = distance;
                    }
                }

                if (bestLength >= 3)
                {
                    var pair = 0x8000 | (bestDistance << 3) | (bestLength - 3);
                    output.Add((byte)(pair >> 8));
                    output.Add((byte)pair);
                    index += bestLength;
                }
                else if (input[index] == ' ' && index + 1 < input.Length && input[index + 1] is >= 0x40 and <= 0x7F)
                {
                    output.Add((byte)(input[index + 1] ^ 0x80));
                    index += 2;
                }
                else if (input[index] is 0 or >= 9 and <= 0x7F)
                {
                    output.Add(input[index++]);
                }
                else
                {
                    var count = 1;
                    while (count < 8 && index + count < input.Length && input[index + count] is >= 1 and <= 8 or >= 0x80) count++;
                    output.Add((byte)count);
                    output.AddRange(input[index..(index + count)]);
                    index += count;
                }
            }

            return [.. output];
        }

        // An index's first record: its header and the tag table.
        private static byte[] IndexHeader(byte[][] tags)
        {
            var record = new byte[192 + 12 + tags.Length * 4];
            "INDX"u8.CopyTo(record);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(4), 192);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(24), 1);
            "TAGX"u8.CopyTo(record.AsSpan(192));
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(196), (uint)(12 + tags.Length * 4));
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(200), 1);
            for (var index = 0; index < tags.Length; index++)
            {
                tags[index].CopyTo(record, 204 + index * 4);
            }

            return record;
        }

        // An index record: its entries and their offsets in the IDXT table.
        private static byte[] IndexRecord(byte[][] entries)
        {
            var body = new List<byte>(new byte[192]);
            var offsets = new List<int>();
            foreach (var entry in entries)
            {
                offsets.Add(body.Count);
                body.AddRange(entry);
            }

            var idxt = body.Count;
            body.AddRange("IDXT"u8.ToArray());
            foreach (var offset in offsets)
            {
                body.Add((byte)(offset >> 8));
                body.Add((byte)offset);
            }

            var record = body.ToArray();
            "INDX"u8.CopyTo(record);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(4), 192);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(20), (uint)idxt);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(24), (uint)entries.Length);
            return record;
        }

        // An index entry: its name, one control byte and its values as forward variable-width numbers.
        private static byte[] Entry(string name, byte control, params int[] values)
        {
            var entry = new List<byte> { (byte)name.Length };
            entry.AddRange(Encoding.ASCII.GetBytes(name));
            entry.Add(control);
            foreach (var value in values)
            {
                var groups = new List<byte>();
                var rest = value;
                do
                {
                    groups.Insert(0, (byte)(rest & 0x7F));
                    rest >>= 7;
                }
                while (rest > 0);
                groups[^1] |= 0x80;
                entry.AddRange(groups);
            }

            return [.. entry];
        }
    }
}
