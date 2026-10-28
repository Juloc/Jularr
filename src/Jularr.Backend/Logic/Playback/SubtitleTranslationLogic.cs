using System.Text;
using System.Text.RegularExpressions;

namespace Jularr.Backend.Logic.Playback
{
    // One cue of a WebVTT file: the line with its times and settings, and the lines of text.
    internal sealed record SubtitleCue(string Timing, string Text);

    // The rules that turn a WebVTT subtitle into numbered batches for the local translation model and back into a WebVTT file.
    internal static partial class SubtitleTranslationLogic
    {
        // A batch stays well inside the model's input; cues of one batch give each other context.
        public const int BatchCues = 12;
        public const int MaximumCues = 4000;
        public const int MaximumBatchCharacters = 2400;

        [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
        private static partial Regex Tags();

        [GeneratedRegex(@"^\s*\[(\d{1,4})\]\s?(.*)$", RegexOptions.CultureInvariant)]
        private static partial Regex Numbered();

        [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
        private static partial Regex Spaces();

        // The cues of a WebVTT text; the header and NOTE or STYLE blocks are left out, markup in the text is kept.
        public static IReadOnlyList<SubtitleCue> ParseCues(string vtt)
        {
            var cues = new List<SubtitleCue>();
            foreach (var block in Regex.Split(vtt.Replace("\r\n", "\n").Replace('\r', '\n'), @"\n{2,}"))
            {
                var lines = block.Split('\n');
                var at = Array.FindIndex(lines, line => line.Contains("-->", StringComparison.Ordinal));
                if (at < 0 || at > 1 || lines[0].StartsWith("NOTE", StringComparison.Ordinal))
                {
                    continue;
                }

                var text = string.Join("\n", lines.Skip(at + 1)).Trim();
                if (text.Length > 0 && cues.Count < MaximumCues)
                {
                    cues.Add(new SubtitleCue(lines[at].Trim(), text));
                }
            }

            return cues;
        }

        // What the model reads of a cue: its words on one line, without markup.
        public static string PlainText(string cueText) => Spaces().Replace(Tags().Replace(cueText, " "), " ").Trim();

        // Cues in groups that stay below the character limit and the cue count.
        public static IReadOnlyList<IReadOnlyList<int>> Batches(IReadOnlyList<SubtitleCue> cues)
        {
            var batches = new List<IReadOnlyList<int>>();
            var current = new List<int>();
            var length = 0;
            for (var index = 0; index < cues.Count; index++)
            {
                // A cue without words (a sound mark, a drawing) stays as it is.
                var size = PlainText(cues[index].Text).Length + 8;
                if (size == 8)
                {
                    continue;
                }

                if (current.Count > 0 && (current.Count >= BatchCues || length + size > MaximumBatchCharacters))
                {
                    batches.Add(current);
                    current = [];
                    length = 0;
                }

                current.Add(index);
                length += size;
            }

            if (current.Count > 0)
            {
                batches.Add(current);
            }

            return batches;
        }

        // The numbered lines the model translates: "[1] text", one cue per line.
        public static string BatchText(IReadOnlyList<SubtitleCue> cues, IReadOnlyList<int> batch)
        {
            var builder = new StringBuilder();
            for (var number = 0; number < batch.Count; number++)
            {
                builder.Append('[').Append(number + 1).Append("] ").Append(PlainText(cues[batch[number]].Text)).Append('\n');
            }

            return builder.ToString().TrimEnd();
        }

        // The model's answer by number; null when a cue is missing, so the caller translates the cues one by one instead.
        public static string[]? ParseBatch(string answer, int count)
        {
            var result = new string?[count];
            foreach (var line in answer.Replace("\r\n", "\n").Split('\n'))
            {
                var match = Numbered().Match(line);
                if (match.Success && int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) is var number && number >= 1 && number <= count && result[number - 1] is null)
                {
                    result[number - 1] = match.Groups[2].Value.Trim();
                }
            }

            return result.All(text => !string.IsNullOrWhiteSpace(text)) ? result!.ToArray() : null;
        }

        // The translated cues with the times of the original ones; a cue without a translation keeps its own text.
        public static string Compose(IReadOnlyList<SubtitleCue> cues, IReadOnlyList<string?> translations)
        {
            var builder = new StringBuilder("WEBVTT\n\n");
            for (var index = 0; index < cues.Count; index++)
            {
                builder.Append(cues[index].Timing).Append('\n').Append((translations[index] ?? cues[index].Text).Replace("-->", "->", StringComparison.Ordinal)).Append("\n\n");
            }

            return builder.ToString();
        }

        // A key of a translated subtitle: "x-" the target language, "-" the key of the source track.
        public static string Key(string language, string sourceKey) => "x-" + language + "-" + sourceKey;

        public static bool TryParseKey(string key, out string language, out string sourceKey)
        {
            language = sourceKey = "";
            var parts = key.Split('-', 3);
            if (parts.Length != 3 || parts[0] != "x" || parts[1].Length != 2 || !parts[1].All(char.IsAsciiLetterLower) || parts[2].Length == 0)
            {
                return false;
            }

            language = parts[1];
            sourceKey = parts[2];
            return true;
        }
    }
}
