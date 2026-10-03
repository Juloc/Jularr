using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.MediaSegments;

public sealed record SidecarSegment(
    MediaSegmentKind Kind,
    long StartMs,
    long EndMs,
    double Confidence);

public sealed record SidecarEpisodeEntry(
    int SeasonNumber,
    int EpisodeNumber,
    IReadOnlyList<SidecarSegment> Segments);

public sealed record SidecarParseResult(
    IReadOnlyList<SidecarSegment> Segments,
    IReadOnlyList<SidecarEpisodeEntry> Episodes,
    IReadOnlyList<string> Warnings,
    string? Error)
{
    public bool IsValid => Error is null;
}

// Documented sidecar format (docs/MEDIA_SEGMENTS.md):
//   <media file name>.segments.json  -> { "version": 1, "segments": [ ... ] }
//   segments.json (anime or season folder) -> { "version": 1, "episodes": [ { "season", "episode", "segments" } ] }
// Segment: { "kind": "intro|recap|outro|preview|credits", "startMs": 90000, "endMs": 180000, "confidence": 1.0 }
// "start"/"end" timecodes (1:30.500) are accepted instead of the millisecond fields.
public static class MediaSegmentSidecar
{
    public const string EpisodeSuffix = ".segments.json";
    public const string AnimeFileName = "segments.json";
    public const int SchemaVersion = 1;
    public const long MaxFileBytes = 1024 * 1024;

    public static string EpisodeSidecarPath(string mediaPath)
    {
        var directory = Path.GetDirectoryName(mediaPath) ?? "";
        return Path.Combine(
            directory,
            Path.GetFileNameWithoutExtension(mediaPath) + EpisodeSuffix);
    }

    public static SidecarParseResult Parse(string json)
    {
        var warnings = new List<string>();

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("The sidecar root must be a JSON object.");
            }

            if (root.TryGetProperty("version", out var version) &&
                (version.ValueKind != JsonValueKind.Number ||
                 !version.TryGetInt32(out var parsedVersion) ||
                 parsedVersion != SchemaVersion))
            {
                return Invalid($"Unsupported sidecar version; expected {SchemaVersion}.");
            }

            var segments = root.TryGetProperty("segments", out var segmentsElement)
                ? ParseSegments(segmentsElement, "segments", warnings)
                : [];

            var episodes = new List<SidecarEpisodeEntry>();
            if (root.TryGetProperty("episodes", out var episodesElement))
            {
                if (episodesElement.ValueKind != JsonValueKind.Array)
                {
                    warnings.Add("\"episodes\" must be an array; ignored.");
                }
                else
                {
                    var index = 0;
                    foreach (var entry in episodesElement.EnumerateArray())
                    {
                        var label = $"episodes[{index++}]";
                        if (entry.ValueKind != JsonValueKind.Object ||
                            !TryReadInt(entry, "episode", out var episodeNumber) ||
                            episodeNumber < 0)
                        {
                            warnings.Add($"{label} needs a non-negative \"episode\" number; ignored.");
                            continue;
                        }

                        var seasonNumber = TryReadInt(entry, "season", out var season) && season >= 0
                            ? season
                            : 1;

                        var entrySegments = entry.TryGetProperty("segments", out var entrySegmentsElement)
                            ? ParseSegments(entrySegmentsElement, label, warnings)
                            : [];

                        episodes.Add(new SidecarEpisodeEntry(
                            seasonNumber,
                            episodeNumber,
                            entrySegments));
                    }
                }
            }

            return new SidecarParseResult(segments, episodes, warnings, null);
        }
        catch (JsonException exception)
        {
            return Invalid($"Invalid JSON: {exception.Message}");
        }

        SidecarParseResult Invalid(string error) =>
            new([], [], warnings, error);
    }

    // Later entries for the same kind replace earlier ones; the file stays one marker per kind.
    private static IReadOnlyList<SidecarSegment> ParseSegments(
        JsonElement element,
        string label,
        List<string> warnings)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            warnings.Add($"{label} must be an array; ignored.");
            return [];
        }

        var byKind = new Dictionary<MediaSegmentKind, SidecarSegment>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemLabel = $"{label}[{index++}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"{itemLabel} must be an object; ignored.");
                continue;
            }

            if (!MediaSegmentPolicy.TryParseKind(ReadString(item, "kind"), out var kind))
            {
                warnings.Add($"{itemLabel} has an unknown \"kind\"; ignored.");
                continue;
            }

            if (!TryReadTime(item, "startMs", "start", out var startMs) ||
                !TryReadTime(item, "endMs", "end", out var endMs))
            {
                warnings.Add($"{itemLabel} needs \"startMs\"/\"endMs\" (or \"start\"/\"end\" timecodes); ignored.");
                continue;
            }

            if (!MediaSegmentPolicy.IsValidRange(startMs, endMs))
            {
                warnings.Add($"{itemLabel} must end at least {MediaSegmentPolicy.MinimumLengthMs} ms after it starts; ignored.");
                continue;
            }

            var confidence = 1.0;
            if (item.TryGetProperty("confidence", out var confidenceElement))
            {
                if (confidenceElement.ValueKind != JsonValueKind.Number ||
                    !confidenceElement.TryGetDouble(out confidence))
                {
                    warnings.Add($"{itemLabel} has a non-numeric \"confidence\"; ignored.");
                    continue;
                }

                confidence = MediaSegmentPolicy.ClampConfidence(confidence);
            }

            byKind[kind] = new SidecarSegment(kind, startMs, endMs, confidence);
        }

        return byKind.Values
            .OrderBy(x => x.StartMs)
            .ThenBy(x => x.Kind)
            .ToArray();
    }

    private static bool TryReadTime(
        JsonElement element,
        string millisecondsName,
        string timecodeName,
        out long milliseconds)
    {
        milliseconds = 0;
        if (element.TryGetProperty(millisecondsName, out var value))
        {
            return value.ValueKind == JsonValueKind.Number &&
                   value.TryGetInt64(out milliseconds) &&
                   milliseconds >= 0;
        }

        if (element.TryGetProperty(timecodeName, out var timecode))
        {
            return timecode.ValueKind switch
            {
                JsonValueKind.String => MediaTimecode.TryParse(timecode.GetString(), out milliseconds),
                JsonValueKind.Number => MediaTimecode.TryParse(timecode.GetRawText(), out milliseconds),
                _ => false
            };
        }

        return false;
    }

    private static bool TryReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed record SidecarImportResult(
    int Imported,
    int Updated,
    int Removed,
    int Warnings);

// Reconciles imported markers with the sidecar files next to the media of one
// library root. Runs inside the regular library scan; there is no second scanner.
// The sidecar is the source of truth for Source=Imported rows: a marker that
// disappears from the file is removed, manual/provider/detector rows are untouched.
public sealed class MediaSegmentSidecarImporter(
    AppDbContext db,
    ILogger<MediaSegmentSidecarImporter> logger)
{
    public async Task<SidecarImportResult> ReconcileRootAsync(
        Guid rootId,
        CancellationToken cancellationToken)
    {
        var rootPath = await db.LibraryRoots
            .AsNoTracking()
            .Where(x => x.Id == rootId)
            .Select(x => x.Path)
            .SingleOrDefaultAsync(cancellationToken);

        if (rootPath is null)
        {
            return new SidecarImportResult(0, 0, 0, 0);
        }

        rootPath = Path.GetFullPath(rootPath);

        var mediaRows = await (
            from media in db.MediaFiles.AsNoTracking()
            join episode in db.Episodes.AsNoTracking() on media.EpisodeId equals episode.Id
            where media.LibraryRootId == rootId
            orderby media.Path
            select new MediaRow(
                episode.Id,
                media.Path,
                episode.SeasonNumber,
                episode.Number))
            .ToListAsync(cancellationToken);

        var existing = await db.EpisodeMediaSegments
            .Where(segment =>
                segment.Source == MediaSegmentSource.Imported &&
                db.MediaFiles.Any(media =>
                    media.LibraryRootId == rootId &&
                    media.EpisodeId == segment.EpisodeId))
            .ToListAsync(cancellationToken);

        var existingByKey = existing.ToDictionary(x => (x.EpisodeId, x.Kind));
        var desiredKeys = new HashSet<(Guid EpisodeId, MediaSegmentKind Kind)>();
        var directoryFiles = new Dictionary<string, SidecarFile>(StringComparer.Ordinal);
        var listings = new SubtitleSidecarDirectoryCache();
        var imported = 0;
        var updated = 0;
        var warnings = 0;
        var now = DateTime.UtcNow;

        foreach (var episodeMedia in mediaRows.GroupBy(x => x.EpisodeId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var segments = ResolveEpisodeSegments(
                rootPath,
                episodeMedia.ToArray(),
                directoryFiles,
                listings,
                ref warnings);

            if (segments is null)
            {
                // A sidecar exists but could not be read or parsed: keep what was
                // imported last time instead of flapping on a bad read.
                foreach (var row in existing.Where(x => x.EpisodeId == episodeMedia.Key))
                {
                    desiredKeys.Add((row.EpisodeId, row.Kind));
                }

                continue;
            }

            foreach (var segment in segments)
            {
                var key = (episodeMedia.Key, segment.Kind);
                desiredKeys.Add(key);

                if (existingByKey.TryGetValue(key, out var row))
                {
                    if (row.StartMs == segment.StartMs &&
                        row.EndMs == segment.EndMs &&
                        row.Confidence == segment.Confidence &&
                        row.Method == MediaSegmentPolicy.SidecarMethod &&
                        row.Version == MediaSegmentPolicy.SidecarVersion)
                    {
                        continue;
                    }

                    row.StartMs = segment.StartMs;
                    row.EndMs = segment.EndMs;
                    row.Confidence = segment.Confidence;
                    row.Method = MediaSegmentPolicy.SidecarMethod;
                    row.Version = MediaSegmentPolicy.SidecarVersion;
                    row.UpdatedAt = now;
                    updated++;
                    continue;
                }

                db.EpisodeMediaSegments.Add(new EpisodeMediaSegment
                {
                    EpisodeId = episodeMedia.Key,
                    Kind = segment.Kind,
                    StartMs = segment.StartMs,
                    EndMs = segment.EndMs,
                    Source = MediaSegmentSource.Imported,
                    Method = MediaSegmentPolicy.SidecarMethod,
                    Version = MediaSegmentPolicy.SidecarVersion,
                    Confidence = segment.Confidence,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                imported++;
            }
        }

        var stale = existing
            .Where(x => !desiredKeys.Contains((x.EpisodeId, x.Kind)))
            .ToArray();
        if (stale.Length > 0)
        {
            db.EpisodeMediaSegments.RemoveRange(stale);
        }

        if (imported > 0 || updated > 0 || stale.Length > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        if (imported > 0 || updated > 0 || stale.Length > 0 || warnings > 0)
        {
            logger.LogInformation(
                "Segment sidecar reconciliation for {Root}: {Imported} imported, {Updated} updated, {Removed} removed, {Warnings} warnings.",
                rootPath,
                imported,
                updated,
                stale.Length,
                warnings);
        }

        return new SidecarImportResult(imported, updated, stale.Length, warnings);
    }

    // Precedence: the first episode sidecar (ordinal media path order) wins completely;
    // otherwise segments.json in the media folder, then in the anime's top-level folder.
    // Returns null when a relevant sidecar exists but is unusable right now.
    private IReadOnlyList<SidecarSegment>? ResolveEpisodeSegments(
        string rootPath,
        IReadOnlyList<MediaRow> episodeMedia,
        Dictionary<string, SidecarFile> directoryFiles,
        SubtitleSidecarDirectoryCache listings,
        ref int warnings)
    {
        foreach (var media in episodeMedia)
        {
            var sidecar = ReadFile(MediaSegmentSidecar.EpisodeSidecarPath(media.Path), listings, ref warnings);
            if (sidecar.Parsed is not null)
            {
                return sidecar.Parsed.Segments;
            }

            if (sidecar.Failed)
            {
                return null;
            }
        }

        var first = episodeMedia[0];
        foreach (var directory in CandidateDirectories(rootPath, first.Path))
        {
            var path = Path.Combine(directory, MediaSegmentSidecar.AnimeFileName);
            if (!directoryFiles.TryGetValue(path, out var sidecar))
            {
                sidecar = ReadFile(path, listings, ref warnings);
                directoryFiles[path] = sidecar;
            }

            if (sidecar.Failed)
            {
                return null;
            }

            var entry = sidecar.Parsed?.Episodes.FirstOrDefault(x =>
                x.SeasonNumber == first.SeasonNumber &&
                x.EpisodeNumber == first.EpisodeNumber);
            if (entry is not null)
            {
                return entry.Segments;
            }
        }

        return [];
    }

    private static IEnumerable<string> CandidateDirectories(
        string rootPath,
        string mediaPath)
    {
        var mediaDirectory = Path.GetDirectoryName(mediaPath);
        if (mediaDirectory is not null)
        {
            yield return mediaDirectory;
        }

        var relative = Path.GetRelativePath(rootPath, mediaPath);
        var parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            yield break;
        }

        var animeDirectory = Path.GetFullPath(Path.Combine(rootPath, parts[0]));
        if (!string.Equals(animeDirectory, mediaDirectory, StringComparison.Ordinal))
        {
            yield return animeDirectory;
        }
    }

    // Existence is answered from one directory listing per folder and pass. A folder that
    // cannot be listed (offline share) counts as unusable, so imported markers are kept.
    private SidecarFile ReadFile(
        string path,
        SubtitleSidecarDirectoryCache listings,
        ref int warnings)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (directory is null ||
                !listings.GetFiles(directory).Contains(fullPath, StringComparer.Ordinal))
            {
                return SidecarFile.Missing;
            }

            var info = new FileInfo(fullPath);

            if (info.Length > MediaSegmentSidecar.MaxFileBytes)
            {
                warnings++;
                logger.LogWarning(
                    "Ignored segment sidecar {Path}: larger than {Limit} bytes. Existing imported markers are kept.",
                    path,
                    MediaSegmentSidecar.MaxFileBytes);
                return SidecarFile.Unusable;
            }

            var parsed = MediaSegmentSidecar.Parse(File.ReadAllText(path));
            if (!parsed.IsValid)
            {
                warnings++;
                logger.LogWarning(
                    "Ignored segment sidecar {Path}: {Reason} Existing imported markers are kept.",
                    path,
                    parsed.Error);
                return SidecarFile.Unusable;
            }

            foreach (var warning in parsed.Warnings)
            {
                warnings++;
                logger.LogWarning(
                    "Segment sidecar {Path}: {Warning}",
                    path,
                    warning);
            }

            return new SidecarFile(parsed, false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            warnings++;
            logger.LogWarning(
                exception,
                "Segment sidecar {Path} could not be read; existing imported markers are kept.",
                path);
            return SidecarFile.Unusable;
        }
    }

    private sealed record SidecarFile(SidecarParseResult? Parsed, bool Failed)
    {
        public static SidecarFile Missing { get; } = new(null, false);

        public static SidecarFile Unusable { get; } = new(null, true);
    }

    private sealed record MediaRow(
        Guid EpisodeId,
        string Path,
        int SeasonNumber,
        int EpisodeNumber);
}
