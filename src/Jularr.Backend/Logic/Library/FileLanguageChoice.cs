using System.Text.Json;
using System.Text.Json.Nodes;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;

namespace Jularr.Backend.Logic.Library
{
    // The audio and subtitle languages a person chose for one file: three-letter codes of FileLanguages, in the order of that catalog.
    internal sealed record ChosenLanguages(string[] Audio, string[] Subtitles)
    {
        public bool IsEmpty => Audio.Length == 0 && Subtitles.Length == 0;
    }

    // A person's language choice outranks what a scan reads from the streams of a file.
    internal static class FileLanguageChoice
    {
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase) { ["deu"] = "ger", ["fra"] = "fre", ["nld"] = "dut", ["zho"] = "chi" };

        // The choice of one file, or null when it leaves both open; a code the catalog does not know is refused.
        public static ChosenLanguages? Read(string field, IEnumerable<string>? audio, IEnumerable<string>? subtitles)
        {
            var choice = new ChosenLanguages(Known(field, audio), Known(field, subtitles));
            return choice.IsEmpty ? null : choice;
        }

        private static string[] Known(string field, IEnumerable<string>? codes)
        {
            var wanted = (codes ?? []).Select(code => code.Trim().ToLowerInvariant()).Where(code => code.Length > 0).ToHashSet(StringComparer.Ordinal);
            if (wanted.Any(code => !FileLanguages.Known.Any(known => known.Three == code)))
            {
                throw new DataValidationException(field, "invalid_value");
            }

            return [.. FileLanguages.Known.Select(known => known.Three).Where(wanted.Contains)];
        }

        // One chosen language is the language of every track of its kind. Several are spread over the tracks: a track whose stream already names one of them keeps it, the others take the
        // chosen languages no track names, in order; tracks left over keep what their stream says.
        public static string?[] Apply(IReadOnlyList<string?> streams, IReadOnlyList<string> chosen)
        {
            if (chosen.Count == 0)
            {
                return [.. streams];
            }

            if (chosen.Count == 1)
            {
                return [.. streams.Select(_ => (string?)chosen[0])];
            }

            var named = streams.Select(stream => stream is null ? null : Aliases.GetValueOrDefault(stream, stream.ToLowerInvariant())).ToArray();
            var open = new Queue<string>(chosen.Where(language => !named.Contains(language)));
            return [.. streams.Select((stream, index) => named[index] is { } name && chosen.Contains(name) ? name : open.Count > 0 ? open.Dequeue() : stream)];
        }

        // The tracks of a probe, as the JSON the scan saves, with the audio and subtitle languages a person chose.
        public static string ApplyToTracks(string tracks, ChosenLanguages chosen)
        {
            var rows = JsonNode.Parse(tracks)?.AsArray() ?? throw new JsonException();
            foreach (var (type, languages) in new[] { (2, chosen.Audio), (3, chosen.Subtitles) })
            {
                var ofKind = rows.OfType<JsonObject>().Where(row => row["MediaTrackTypeId"]?.GetValue<int>() == type).OrderBy(row => row["StreamIndex"]!.GetValue<int>()).ToArray();
                var applied = Apply([.. ofKind.Select(row => row["Language"]?.GetValue<string>())], languages);
                for (var index = 0; index < ofKind.Length; index++)
                {
                    ofKind[index]["Language"] = applied[index];
                }
            }

            return rows.ToJsonString();
        }
    }
}
