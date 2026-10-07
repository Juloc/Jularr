using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Music;

/// <summary>An audio file of a download and the album track it is, or null when it matches none (a bonus track, an intro).</summary>
public sealed record MusicFileMatch(CompletedDownloadFile File, WorkTrack? Track);

/// <summary>
/// Matches the audio files of a download to the tracks of an album without reading tags: the disc folder (<c>CD1</c>, <c>Disc 2</c>), the
/// leading track number of the file name (<c>01 - Title</c>, <c>1-05 Title</c>) and the title. A number alone is enough on a single-disc
/// album; on several discs the disc or the title has to agree. Every track is used at most once, so a pack with the same file twice never
/// fills two tracks.
/// </summary>
public static partial class MusicTrackMatcher
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) { ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wv", ".ape" };

    [GeneratedRegex(@"^\s*(?:(?<disc>\d{1,2})[-.](?=\d{2}\b))?(?<number>\d{1,3})(?!\d)[\s._-]*(?<title>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingNumberRegex();

    [GeneratedRegex(@"(?:^|[\s\-_.\[(])(?:cd|disc|disk)[\s._-]*(?<disc>\d{1,2})(?=$|[\s\-_.\])])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscFolderRegex();

    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    public static IReadOnlyList<MusicFileMatch> Match(IReadOnlyList<CompletedDownloadFile> files, IReadOnlyList<WorkTrack> tracks)
    {
        var singleDisc = tracks.Select(track => track.Disc).Distinct().Count() <= 1;
        var parsed = files.Select(file => (File: file, Info: Parse(file.Path))).ToArray();
        var claimed = new HashSet<Guid>();
        var matches = new Dictionary<string, WorkTrack?>(StringComparer.Ordinal);

        // The best-scoring pairs first, so a perfect match is never taken away by a weaker file that was listed earlier.
        var pairs = parsed
            .SelectMany(item => tracks.Select(track => (item.File, Track: track, Score: Score(item.Info, track, singleDisc))))
            .Where(pair => pair.Score > 0)
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => pair.File.Path, StringComparer.Ordinal)
            .ThenBy(pair => pair.Track.Disc)
            .ThenBy(pair => pair.Track.Number);
        foreach (var (file, track, _) in pairs)
        {
            if (matches.ContainsKey(file.Path) || !claimed.Add(track.Id))
            {
                continue;
            }

            matches[file.Path] = track;
        }

        return [.. files.Select(file => new MusicFileMatch(file, matches.GetValueOrDefault(file.Path)))];
    }

    /// <summary>
    /// The tracks of an album the provider knows no track list for: one per audio file, numbered by its file name (or by its position), so the
    /// files can still be recorded. They are real structure from then on and never replaced by a later provider list.
    /// </summary>
    public static IReadOnlyList<WorkTrack> Synthesize(Guid workId, IReadOnlyList<CompletedDownloadFile> files)
    {
        var used = new HashSet<(int Disc, int Number)>();
        var tracks = new List<WorkTrack>();
        var ordinal = 0;
        foreach (var file in files.Where(file => IsAudio(file.Path)).OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            ordinal++;
            var info = Parse(file.Path);
            var disc = info.Disc ?? 1;
            var number = info.Number ?? ordinal;
            while (!used.Add((disc, number)))
            {
                number++;
            }

            tracks.Add(new WorkTrack { WorkId = workId, Disc = disc, Number = number, Title = info.Title.Length > 0 ? info.Title : $"Track {number}" });
        }

        return tracks;
    }

    private static int Score(FileInfoGuess info, WorkTrack track, bool singleDisc)
    {
        var titleMatches = info.Title.Length > 0 && Normalize(info.Title) == Normalize(track.Title);
        var titleContained = !titleMatches && info.Title.Length >= 4 && (Normalize(track.Title).Contains(Normalize(info.Title)) || Normalize(info.Title).Contains(Normalize(track.Title)) && Normalize(track.Title).Length >= 4);
        var numberMatches = info.Number == track.Number;
        var discMatches = info.Disc is null ? singleDisc : info.Disc == track.Disc;
        if (numberMatches && discMatches)
        {
            return titleMatches ? 10 : titleContained ? 8 : info.Title.Length == 0 || singleDisc ? 6 : 4;
        }

        // A file whose number is off (a skipped track) is still the track when its title says so.
        return titleMatches && (info.Disc is null || info.Disc == track.Disc) ? 5 : 0;
    }

    private static FileInfoGuess Parse(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        int? disc = DiscFolderRegex().Match(folder) is { Success: true } folderMatch ? int.Parse(folderMatch.Groups["disc"].Value) : null;

        // "Artist - Album - 01 - Title": the track number follows the last separator that precedes a number token.
        var candidate = name;
        var segments = name.Split([" - ", " – "], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            if (LeadingNumberRegex().IsMatch(segments[index]) && index > 0 && segments[index].Length <= 3)
            {
                candidate = string.Join(" - ", segments.Skip(index));
                break;
            }
        }

        var match = LeadingNumberRegex().Match(candidate);
        if (!match.Success || !int.TryParse(match.Groups["number"].Value, out var number))
        {
            return new FileInfoGuess(disc, null, name.Trim());
        }

        if (match.Groups["disc"].Success && int.TryParse(match.Groups["disc"].Value, out var namedDisc))
        {
            disc ??= namedDisc;
        }

        var title = match.Groups["title"].Value.Trim(' ', '-', '_', '.');
        return new FileInfoGuess(disc, number, title);
    }

    private static string Normalize(string value) => new([.. value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    private readonly record struct FileInfoGuess(int? Disc, int? Number, string Title);
}
