using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jularr.Backend.Logic.Reading
{
    // A Rich Text document: paragraphs with bold and italic, headings by their outline level or heading style, PNG and JPEG pictures, the title from its info.
    // Tables, fields' instructions, headers, footers and footnotes are left out.
    internal static partial class RtfBook
    {
        private const int MaximumDepth = 512;

        // Destinations whose text is no part of the book.
        private static readonly HashSet<string> Skipped =
        [
            "fonttbl", "colortbl", "header", "headerl", "headerr", "headerf", "footer", "footerl", "footerr", "footerf", "footnote", "fldinst", "listtable", "listoverridetable",
            "rsidtbl", "generator", "xmlnstbl", "themedata", "colorschememapping", "datastore", "latentstyles", "pgdsctbl", "object", "nonshppict", "bkmkstart", "bkmkend",
            "revtbl", "filetbl", "userprops", "docvar", "pntext", "pntxta", "pntxtb", "annotation", "atnid", "atnauthor", "comment", "xe", "tc", "objdata", "fldtype"
        ];

        private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
        {
            ["emdash"] = "—", ["endash"] = "–", ["lquote"] = "‘", ["rquote"] = "’", ["ldblquote"] = "“", ["rdblquote"] = "”",
            ["bullet"] = "•", ["emspace"] = " ", ["enspace"] = " ", ["qmspace"] = " ", ["tab"] = " ", ["cell"] = " "
        };

        [GeneratedRegex(@"^\s*(heading|überschrift|titre|título|titolo)\s*(\d)", RegexOptions.IgnoreCase)]
        private static partial Regex HeadingStyle();

        private enum Destination
        {
            Text,
            Skip,
            Info,
            Title,
            Picture,
            Styles
        }

        private struct State
        {
            public Destination Destination;
            public bool Bold;
            public bool Italic;
            public bool Ignorable;
            public int UnicodeSkip;
            public int Style;
            public int? Outline;
            public string? PictureType;
        }

        public static ConvertedBook Read(byte[] data)
        {
            if (data.Length < 5 || !data.AsSpan(0, 5).SequenceEqual("{\\rtf"u8))
            {
                throw new InvalidDataException("The file is no RTF.");
            }

            var book = new ConvertedBook();
            var encoding = TextBook.CodePage(1252);
            var headingStyles = new Dictionary<int, int>();
            var stack = new Stack<State>();
            var state = new State { UnicodeSkip = 1 };
            var runs = new List<(string Text, bool Bold, bool Italic, bool Html)>();
            var pending = new List<byte>();
            var title = new StringBuilder();
            var styleName = new StringBuilder();
            var picture = new StringBuilder();
            var pictures = 0;
            var skipCharacters = 0;

            // Bytes of a code page wait until the text around them is complete, so double-byte pages decode too.
            void Flush()
            {
                if (pending.Count == 0) return;
                Text(encoding.GetString([.. pending]));
                pending.Clear();
            }

            void Text(string text)
            {
                switch (state.Destination)
                {
                    case Destination.Text:
                        runs.Add((text, state.Bold, state.Italic, false));
                        break;
                    case Destination.Title:
                        title.Append(text);
                        break;
                    case Destination.Styles:
                        // A style's name ends with a semicolon; "heading 2" makes its number a heading of level 2.
                        foreach (var character in text)
                        {
                            if (character != ';')
                            {
                                styleName.Append(character);
                                continue;
                            }

                            if (HeadingStyle().Match(styleName.ToString()) is { Success: true } match)
                            {
                                headingStyles[state.Style] = match.Groups[2].Value[0] - '0';
                            }

                            styleName.Clear();
                        }

                        break;
                }
            }

            void Paragraph()
            {
                Flush();
                var plain = string.Concat(runs.Where(run => !run.Html).Select(run => run.Text)).Trim();
                if (plain.Length == 0 && !runs.Any(run => run.Html))
                {
                    runs.Clear();
                    return;
                }

                var level = state.Outline is { } outline and < 6 ? outline + 1 : headingStyles.TryGetValue(state.Style, out var styled) ? styled : 0;
                if (level is >= 1 and <= 3 && plain.Length is > 0 and <= 300)
                {
                    book.Chapter(string.Join(' ', plain.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), level - 1);
                }

                var tag = level is >= 1 and <= 6 && plain.Length is > 0 and <= 300 ? "h" + level.ToString(CultureInfo.InvariantCulture) : "p";
                var html = new StringBuilder("<" + tag + ">");
                foreach (var group in Merge(runs))
                {
                    var text = group.Html ? group.Text : BookConversion.Escape(group.Text);
                    html.Append(group.Bold ? "<strong>" : "").Append(group.Italic ? "<em>" : "").Append(text).Append(group.Italic ? "</em>" : "").Append(group.Bold ? "</strong>" : "");
                }

                book.Add(html.Append("</" + tag + ">").ToString());
                runs.Clear();
            }

            var index = 0;
            while (index < data.Length)
            {
                var current = data[index];
                if (current == '{')
                {
                    Flush();
                    if (stack.Count >= MaximumDepth)
                    {
                        throw new InvalidDataException("The RTF nests too deeply.");
                    }

                    stack.Push(state);
                    state.Ignorable = false;
                    index++;
                    continue;
                }

                if (current == '}')
                {
                    Flush();

                    // A picture's hexadecimal bytes become the picture where it stood.
                    if (state.Destination == Destination.Picture && (stack.Count == 0 || stack.Peek().Destination != Destination.Picture))
                    {
                        if (state.PictureType is not null && picture.Length >= 16 && book.Image("rtf" + (pictures++).ToString(CultureInfo.InvariantCulture), () => Hex(picture)) is { } path)
                        {
                            runs.Add(("<img src=\"" + path + "\" alt=\"\"/>", false, false, true));
                        }

                        picture.Clear();
                    }

                    if (stack.Count == 0)
                    {
                        break;
                    }

                    state = stack.Pop();
                    index++;
                    continue;
                }

                if (current == '\\' && index + 1 < data.Length)
                {
                    var next = data[index + 1];
                    if (char.IsAsciiLetter((char)next))
                    {
                        // A control word, its optional number and the one space that ends it.
                        var start = index + 1;
                        var end = start;
                        while (end < data.Length && end - start < 32 && char.IsAsciiLetter((char)data[end])) end++;
                        var word = Encoding.ASCII.GetString(data, start, end - start);
                        int? parameter = null;
                        var numberStart = end;
                        if (end < data.Length && (data[end] == '-' || char.IsAsciiDigit((char)data[end])))
                        {
                            end++;
                            while (end < data.Length && end - numberStart < 11 && char.IsAsciiDigit((char)data[end])) end++;
                            parameter = int.TryParse(Encoding.ASCII.GetString(data, numberStart, end - numberStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : 0;
                        }

                        if (end < data.Length && data[end] == ' ') end++;
                        index = end;

                        if (word == "bin")
                        {
                            index = (int)Math.Min(data.Length, (long)index + Math.Max(0, parameter ?? 0));
                            continue;
                        }

                        Flush();
                        Control(word, parameter);
                        continue;
                    }

                    index += 2;
                    switch ((char)next)
                    {
                        case '\'':
                            if (index + 2 <= data.Length && byte.TryParse(Encoding.ASCII.GetString(data, index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                            {
                                if (skipCharacters > 0) skipCharacters--;
                                else if (state.Destination != Destination.Picture) pending.Add(value);
                                index += 2;
                            }

                            break;
                        case '*':
                            state.Ignorable = true;
                            break;
                        case '\\' or '{' or '}':
                            Flush();
                            Text(((char)next).ToString());
                            break;
                        case '~':
                            Flush();
                            Text(" ");
                            break;
                        case '_':
                            Flush();
                            Text("‑");
                            break;
                        case '\n' or '\r':
                            Paragraph();
                            break;
                    }

                    continue;
                }

                index++;
                if (current is (byte)'\r' or (byte)'\n')
                {
                    continue;
                }

                if (state.Destination == Destination.Picture)
                {
                    if (state.PictureType is not null && char.IsAsciiHexDigit((char)current) && picture.Length < BookConversion.MaximumPictureBytes * 2) picture.Append((char)current);
                    continue;
                }

                if (skipCharacters > 0)
                {
                    skipCharacters--;
                    continue;
                }

                if (state.Destination is not (Destination.Skip or Destination.Info))
                {
                    pending.Add(current);
                }
            }

            Paragraph();
            book.Title = string.Join(' ', title.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) is { Length: > 0 } named ? named : null;
            return book;

            void Control(string word, int? parameter)
            {
                // An unknown destination behind \* is skipped with everything inside it, also inside a picture (its id and properties are no picture bytes).
                if (state.Ignorable && word is not ("shppict" or "pict" or "title"))
                {
                    state.Ignorable = false;
                    if (state.Destination is Destination.Text or Destination.Title or Destination.Picture) state.Destination = Destination.Skip;
                    return;
                }

                state.Ignorable = false;
                if (state.Destination == Destination.Skip)
                {
                    return;
                }

                if (Skipped.Contains(word))
                {
                    state.Destination = Destination.Skip;
                    return;
                }

                switch (word)
                {
                    case "info":
                        state.Destination = Destination.Info;
                        return;
                    case "title":
                        state.Destination = state.Destination == Destination.Info ? Destination.Title : Destination.Skip;
                        return;
                    case "stylesheet":
                        state.Destination = Destination.Styles;
                        return;
                    case "pict":
                        state.Destination = Destination.Picture;
                        state.PictureType = null;
                        return;
                    case "pngblip" or "jpegblip":
                        if (state.Destination == Destination.Picture) state.PictureType = word;
                        return;
                    case "ansicpg":
                        encoding = TextBook.CodePage(parameter ?? 1252);
                        return;
                }

                if (state.Destination is Destination.Skip or Destination.Info or Destination.Picture)
                {
                    return;
                }

                switch (word)
                {
                    case "par" or "sect" or "page" or "row":
                        if (state.Destination == Destination.Text) Paragraph();
                        break;
                    case "line":
                        if (state.Destination == Destination.Text) runs.Add(("<br/>", false, false, true));
                        break;
                    case "pard":
                        state.Style = 0;
                        state.Outline = null;
                        break;
                    case "s":
                        state.Style = parameter ?? 0;
                        break;
                    case "outlinelevel":
                        state.Outline = parameter;
                        break;
                    case "b":
                        state.Bold = parameter is null or not 0;
                        break;
                    case "i":
                        state.Italic = parameter is null or not 0;
                        break;
                    case "plain":
                        state.Bold = false;
                        state.Italic = false;
                        break;
                    case "uc":
                        state.UnicodeSkip = Math.Clamp(parameter ?? 1, 0, 8);
                        break;
                    case "u":
                        Text(((char)(parameter is < 0 ? parameter.Value + 65536 : parameter ?? 0)).ToString());
                        skipCharacters = state.UnicodeSkip;
                        break;
                    default:
                        if (Symbols.TryGetValue(word, out var symbol)) Text(symbol);
                        break;
                }
            }
        }

        // Runs of the same emphasis join.
        private static IEnumerable<(string Text, bool Bold, bool Italic, bool Html)> Merge(List<(string Text, bool Bold, bool Italic, bool Html)> runs)
        {
            var text = new StringBuilder();
            (bool Bold, bool Italic)? style = null;
            foreach (var run in runs)
            {
                var escaped = run.Html ? run.Text : BookConversion.Escape(run.Text);
                if (style == (run.Bold, run.Italic))
                {
                    text.Append(escaped);
                    continue;
                }

                if (style is { } previous) yield return (text.ToString(), previous.Bold, previous.Italic, true);
                text.Clear().Append(escaped);
                style = (run.Bold, run.Italic);
            }

            if (style is { } last) yield return (text.ToString(), last.Bold, last.Italic, true);
        }

        private static byte[] Hex(StringBuilder hex) => Convert.FromHexString(hex.Length % 2 == 0 ? hex.ToString() : hex.ToString(0, hex.Length - 1));
    }
}
