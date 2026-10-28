using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Playback;

namespace Jularr.Backend.Logic.Playback
{
    // One chapter of a video file.
    internal sealed record MediaChapterRow(int ChapterIndex, long StartMs, long EndMs, string? Title);

    // A skippable stretch and where it was found.
    internal sealed record MediaMarkerRow(MediaMarkerKind MediaMarkerKindTypeId, MediaMarkerSource MediaMarkerSourceTypeId, long StartMs, long EndMs, decimal Confidence);

    // The rules that turn chapter titles and sidecar files into markers.
    internal static partial class MediaMarkers
    {
        public const int MaximumChapters = 500;

        // A sidecar file is a few lines; anything larger is not one.
        public const int MaximumSidecarBytes = 65_536;
        public const int MaximumSidecarLines = 200;
        private const decimal MaximumSeconds = 86_400m;
        private const long MaximumIntroMs = 300_000;

        [GeneratedRegex(@"^(recap|previously( on)?|story so far|last time)\b", RegexOptions.CultureInvariant)]
        private static partial Regex RecapPattern();

        [GeneratedRegex(@"^(preview|next (time|episode|on)|coming (up|next)|trailer)\b", RegexOptions.CultureInvariant)]
        private static partial Regex PreviewPattern();

        [GeneratedRegex(@"^(intro|introduction|opening( credits| theme| sequence| titles?)?|op ?\d*|title sequence|main titles?|theme( song)?)\b", RegexOptions.CultureInvariant)]
        private static partial Regex IntroPattern();

        [GeneratedRegex(@"^(((end|ending|closing|final) )?credits?|ending( theme| sequence)?|closing|outro|ed ?\d*)\b", RegexOptions.CultureInvariant)]
        private static partial Regex CreditsPattern();

        [GeneratedRegex(@"[\s_.\-]+", RegexOptions.CultureInvariant)]
        private static partial Regex Separators();

        // What a chapter's title says it is; a title that names nothing known is just a chapter.
        public static MediaMarkerKind? KindOfChapter(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var name = Separators().Replace(title.Trim().ToLowerInvariant(), " ").Trim();
            if (RecapPattern().IsMatch(name))
            {
                return MediaMarkerKind.Recap;
            }

            if (PreviewPattern().IsMatch(name))
            {
                return MediaMarkerKind.Preview;
            }

            // "Opening credits" opens the show; "end credits" close it.
            if (IntroPattern().IsMatch(name))
            {
                return MediaMarkerKind.Intro;
            }

            return CreditsPattern().IsMatch(name) ? MediaMarkerKind.Credits : null;
        }

        // The first run of consecutive chapters of each kind is one marker.
        public static IReadOnlyList<MediaMarkerRow> FromChapters(IReadOnlyList<MediaChapterRow> chapters, long? durationMs)
        {
            var markers = new List<MediaMarkerRow>();
            var ordered = chapters.OrderBy(chapter => chapter.ChapterIndex).Take(MaximumChapters).ToList();
            for (var index = 0; index < ordered.Count; index++)
            {
                if (KindOfChapter(ordered[index].Title) is not { } kind || markers.Any(marker => marker.MediaMarkerKindTypeId == kind))
                {
                    continue;
                }

                var start = ordered[index].StartMs;
                var end = ordered[index].EndMs;
                while (index + 1 < ordered.Count && KindOfChapter(ordered[index + 1].Title) == kind)
                {
                    index++;
                    end = ordered[index].EndMs;
                }

                if (Fit(kind, MediaMarkerSource.Chapter, start, end, 1m, durationMs) is { } marker && IsPlausible(marker, durationMs))
                {
                    markers.Add(marker);
                }
            }

            return markers;
        }

        // The chapters the player lists: a stretch that cannot be what its title says (see below) is left out, and one chapter alone is no list.
        public static IReadOnlyList<MediaChapterRow> UsableChapters(IReadOnlyList<MediaChapterRow> chapters)
        {
            var duration = chapters.Count == 0 ? 0 : chapters.Max(chapter => chapter.EndMs);
            var usable = chapters.Where(chapter => KindOfChapter(chapter.Title) is not { } kind
                || IsPlausible(new MediaMarkerRow(kind, MediaMarkerSource.Chapter, chapter.StartMs, chapter.EndMs, 1m), duration)).ToList();
            return usable.Count >= 2 ? usable : [];
        }

        // A marker a person set or a sidecar names is theirs to decide; one read from the file's chapters must be plausible.
        public static bool IsUsable(MediaMarkerKind kind, MediaMarkerSource source, long startMs, long endMs, long? durationMs) =>
            source != MediaMarkerSource.Chapter || IsPlausible(new MediaMarkerRow(kind, source, startMs, endMs, 1m), durationMs);

        // A file may carry one chapter named "Credits" over the whole episode; a skip over most of the file is a tagging mistake, not a marker.
        // Intro and recap stay short, credits and preview neither begin the file nor cover more than half of it.
        private static bool IsPlausible(MediaMarkerRow marker, long? durationMs)
        {
            var length = marker.EndMs - marker.StartMs;
            if (marker.MediaMarkerKindTypeId is MediaMarkerKind.Intro or MediaMarkerKind.Recap)
            {
                return length <= MaximumIntroMs;
            }

            return marker.StartMs > 0 && (durationMs is not { } duration || length * 2 <= duration);
        }

        // A sidecar file next to the video: one line per marker, "start end action" in seconds, the way Kodi's and Comskip's EDL files are written.
        // Actions 0 to 3 are cuts and commercial breaks and are ignored; 4 is an intro, 5 credits, 6 a recap and 7 a preview.
        public static IReadOnlyList<MediaMarkerRow> ParseEdl(string text, long? durationMs)
        {
            var markers = new List<MediaMarkerRow>();
            var lines = text.Split('\n', StringSplitOptions.None);
            foreach (var raw in lines.Take(MaximumSidecarLines))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || !decimal.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var start)
                    || !decimal.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var end)
                    || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var action)
                    || start > MaximumSeconds || end > MaximumSeconds)
                {
                    continue;
                }

                MediaMarkerKind? kind = action switch { 4 => MediaMarkerKind.Intro, 5 => MediaMarkerKind.Credits, 6 => MediaMarkerKind.Recap, 7 => MediaMarkerKind.Preview, _ => null };
                if (kind is { } known && markers.All(marker => marker.MediaMarkerKindTypeId != known)
                    && Fit(known, MediaMarkerSource.Sidecar, (long)Math.Round(start * 1000m), (long)Math.Round(end * 1000m), 1m, durationMs) is { } marker)
                {
                    markers.Add(marker);
                }
            }

            return markers;
        }

        // The sidecar of a video: its path with the extension changed.
        public static string? SidecarPath(string relativePath)
        {
            var slash = relativePath.LastIndexOf('/');
            var dot = relativePath.LastIndexOf('.');
            return dot > slash + 1 ? relativePath[..dot] + ".edl" : null;
        }

        // A marker lies inside the file; one that ends before it begins, or begins after the file ends, is no marker.
        public static MediaMarkerRow? Fit(MediaMarkerKind kind, MediaMarkerSource source, long startMs, long endMs, decimal confidence, long? durationMs)
        {
            if (startMs < 0 || durationMs is { } duration && startMs >= duration)
            {
                return null;
            }

            if (durationMs is { } length && endMs > length)
            {
                endMs = length;
            }

            return endMs > startMs ? new MediaMarkerRow(kind, source, startMs, endMs, confidence) : null;
        }
    }
}
