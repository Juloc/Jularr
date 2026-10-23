using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Jularr.Integrations.Data.Usenet;

namespace Jularr.Integrations.Logic.Usenet
{
    // NZB files name the articles of each file; message ids go to the news server verbatim, so they are checked strictly.
    internal static partial class NzbReader
    {
        public const long MaximumBytes = 32L * 1024 * 1024;
        private const int MaximumFiles = 10_000;
        private static readonly XNamespace Nzb = "http://www.newzbin.com/DTD/2003/nzb";

        [GeneratedRegex("^[\\x21-\\x7E]{3,250}$")]
        private static partial Regex MessageId();

        [GeneratedRegex("\"([^\"]{1,255})\"")]
        private static partial Regex QuotedName();

        public static IReadOnlyList<NzbFile> Read(Stream stream)
        {
            XDocument document;
            try
            {
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = MaximumBytes });
                document = XDocument.Load(reader);
            }
            catch (XmlException exception)
            {
                throw new UsenetException(UsenetFailure.InvalidResponse, "The NZB is not valid XML.", exception);
            }

            var files = new List<NzbFile>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in (document.Root?.Elements(Nzb + "file") ?? []).Take(MaximumFiles))
            {
                var subject = (string?)file.Attribute("subject") ?? "";
                var segments = (file.Element(Nzb + "segments")?.Elements(Nzb + "segment") ?? [])
                    .Select(segment => (Number: int.TryParse((string?)segment.Attribute("number"), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0,
                        Bytes: long.TryParse((string?)segment.Attribute("bytes"), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ? bytes : 0, Id: segment.Value.Trim().Trim('<', '>')))
                    .Where(segment => segment.Number > 0 && MessageId().IsMatch(segment.Id) && !segment.Id.Contains('<') && !segment.Id.Contains('>'))
                    .GroupBy(segment => segment.Number)
                    .Select(group => group.First())
                    .OrderBy(segment => segment.Number)
                    .Select(segment => new NzbSegment(segment.Number, segment.Bytes, segment.Id))
                    .ToArray();
                if (segments.Length == 0)
                {
                    continue;
                }

                // The quoted subject part is the file name; duplicates get a counter.
                var name = SafeName(QuotedName().Match(subject) is { Success: true } quoted ? quoted.Groups[1].Value : "file" + (files.Count + 1).ToString(CultureInfo.InvariantCulture));
                var unique = name;
                for (var attempt = 2; !names.Add(unique); attempt++)
                {
                    unique = Path.GetFileNameWithoutExtension(name) + " (" + attempt.ToString(CultureInfo.InvariantCulture) + ")" + Path.GetExtension(name);
                }

                files.Add(new NzbFile(unique, segments));
            }

            return files.Count > 0 ? files : throw new UsenetException(UsenetFailure.InvalidResponse, "The NZB names no files.");
        }

        // No folders, no control characters, no device names, no names that only hide.
        private static string SafeName(string value)
        {
            var name = new string(Path.GetFileName(value.Replace('\\', '/')).Select(character => char.IsControl(character) || Path.GetInvalidFileNameChars().Contains(character) || character is ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : character).ToArray()).Trim(' ', '.');
            if (name.Length > 200)
            {
                name = name[..(200 - Path.GetExtension(name).Length)] + Path.GetExtension(name);
            }

            return name.Length == 0 ? "file" : DeviceName().IsMatch(name) ? "_" + name : name;
        }

        [GeneratedRegex(@"^(con|prn|aux|nul|com\d|lpt\d)(\.|$)", RegexOptions.IgnoreCase)]
        private static partial Regex DeviceName();
    }
}
