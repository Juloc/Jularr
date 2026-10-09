using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Jularr.Web.Features.Manga;

public sealed partial class MangaImportService
{
    public const string CacheRoot = "/data/manga-cache";
    private readonly MangaRepository repository;
    private readonly string cacheRoot;

    public MangaImportService(
        MangaRepository repository,
        string? cacheRoot = null)
    {
        this.repository = repository;
        this.cacheRoot = Path.GetFullPath(cacheRoot ?? CacheRoot);
    }
    private const int MaximumPagesPerChapter = 2000;
    private const long MaximumPageBytes = 100L * 1024 * 1024;
    private const long MaximumChapterBytes = 4L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp", ".avif", ".gif"
        };

    private static readonly HashSet<string> ArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".cbz", ".zip"
        };

    /// <summary>A file a completed Manga download may contribute: a CBZ/ZIP archive or a page image.</summary>
    public static bool IsImportableFile(string path) =>
        ArchiveExtensions.Contains(Path.GetExtension(path)) ||
        ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Imports a CBZ/ZIP file or a directory as one series, identified by its path. With
    /// <paramref name="intoSeriesId"/> the chapters are added to that existing series instead (for
    /// example a newly downloaded volume of a series already matched to the same AniList entry);
    /// the series row itself is left unchanged. Either way only chapters below
    /// <paramref name="source"/> that disappeared are removed, never chapters from other folders.
    /// </summary>
    public async Task<MangaImportResult> ImportAsync(
        string source,
        CancellationToken cancellationToken,
        Guid? intoSeriesId = null)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new InvalidOperationException("Choose a manga CBZ/ZIP file or directory.");
        }

        var sourcePath = Path.GetFullPath(source.Trim());
        if (!Directory.Exists(sourcePath) && !File.Exists(sourcePath))
        {
            throw new InvalidOperationException($"Manga source does not exist: {sourcePath}");
        }

        if (File.Exists(sourcePath) &&
            !ArchiveExtensions.Contains(Path.GetExtension(sourcePath)))
        {
            throw new InvalidOperationException("A manga file import must be CBZ or ZIP.");
        }

        // Validate before anything is stored, so an unusable package leaves no empty series.
        var sources = DiscoverChapterSources(sourcePath);
        if (sources.Count == 0)
        {
            throw new InvalidOperationException(
                "No CBZ/ZIP archives or image chapters were found in this manga source.");
        }

        // A stable id, reused across rescans instead of recomputed from the path (#563): an
        // explicit intoSeriesId wins (adding a release to a series already matched elsewhere),
        // otherwise an unmoved/unrenamed series folder is found by its still-current path, and
        // only a genuinely new series gets a freshly minted id.
        var seriesId = intoSeriesId ??
                       await repository.FindSeriesIdByPathAsync(sourcePath, cancellationToken) ??
                       Guid.NewGuid();
        if (intoSeriesId is null)
        {
            var title = File.Exists(sourcePath)
                ? CleanTitle(Path.GetFileNameWithoutExtension(sourcePath))
                : new DirectoryInfo(sourcePath).Name;

            await repository.UpsertSeriesAsync(
                seriesId,
                title,
                sourcePath,
                cancellationToken);
        }

        var existing = await repository.GetChapterSourcesAsync(
            seriesId,
            cancellationToken);
        var existingByPath = existing.ToDictionary(
            x => x.SourcePath,
            StringComparer.Ordinal);
        // Only a record whose file is gone can be the one a new file replaces by rename; a file that is still there next to a new one (a ZIP beside its
        // CBZ) is another version of the same chapter and keeps its own record.
        var currentPaths = sources.Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
        var existingByNumber = new Dictionary<(double Number, int? VolumeNumber), Guid>();
        foreach (var item in existing.Where(x => !currentPaths.Contains(x.SourcePath)))
        {
            existingByNumber.TryAdd((item.Number, item.VolumeNumber), item.Id);
        }

        // Rows from `existing` still claimed by this scan (matched by path or by number/volume);
        // anything left unclaimed afterwards is genuinely gone and gets removed below. A chapter
        // whose file was merely renamed stays claimed under its original id (#563), so it is never
        // treated as stale even though its old path is no longer observed.
        var claimedIds = new HashSet<Guid>();
        var totalPages = 0;
        var updated = 0;

        for (var index = 0; index < sources.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceItem = sources[index];
            var number = TryParseChapterNumber(sourceItem.Path) ?? index + 1;
            var volume = TryParseVolumeNumber(sourceItem.Path);

            Guid chapterId;
            var isExisting = false;
            if (existingByPath.TryGetValue(sourceItem.Path, out var samePath))
            {
                chapterId = samePath.Id;
                isExisting = claimedIds.Add(chapterId);
            }
            else if (existingByNumber.TryGetValue((number, volume), out var sameNumberId) &&
                     claimedIds.Add(sameNumberId))
            {
                // Not seen at this path before, but this series already has a chapter with the
                // same chapter/volume number: a rename, not a new chapter, so its stable id (and
                // everything keyed on it - reading progress, bookmarks) is preserved (#563).
                chapterId = sameNumberId;
                isExisting = true;
            }
            else
            {
                chapterId = Guid.NewGuid();
            }

            var chapterTitle = BuildChapterTitle(sourceItem.Path, number);
            var updatedAt = GetSourceUpdatedAt(sourceItem);
            var cacheDirectory = GetChapterCacheDirectory(seriesId, chapterId);

            var cached = sourceItem.Kind == "archive"
                ? await CacheArchiveAsync(
                    sourceItem.Path,
                    cacheDirectory,
                    chapterId,
                    cancellationToken)
                : await CacheDirectoryAsync(
                    sourceItem.Path,
                    cacheDirectory,
                    chapterId,
                    cancellationToken);

            if (cached.Pages.Count == 0)
            {
                continue;
            }

            Guid? volumeId = volume is { } volumeNumber
                ? await repository.UpsertVolumeAsync(seriesId, volumeNumber, null, cancellationToken)
                : null;

            var chapter = new MangaChapterItem(
                chapterId,
                seriesId,
                number,
                volume,
                chapterTitle,
                cached.Pages.Count,
                sourceItem.Kind,
                updatedAt,
                volumeId);

            await repository.UpsertChapterAsync(
                chapter,
                sourceItem.Path,
                cancellationToken);
            await repository.ReplacePagesAsync(
                chapterId,
                cached.Pages,
                cached.SourceEntries,
                cancellationToken);

            totalPages += cached.Pages.Count;
            if (isExisting)
            {
                updated++;
            }
        }

        foreach (var stale in existing.Where(x =>
                     !claimedIds.Contains(x.Id) &&
                     IsSameOrBelow(x.SourcePath, sourcePath)))
        {
            await repository.RemoveChapterAsync(stale.Id, cancellationToken);
            var staleCache = GetChapterCacheDirectory(seriesId, stale.Id);
            TryDeleteDirectory(staleCache);
        }

        return new MangaImportResult(
            seriesId,
            sources.Count,
            totalPages,
            updated);
    }

    private static List<ChapterSource> DiscoverChapterSources(string sourcePath)
    {
        if (File.Exists(sourcePath))
        {
            return
            [
                new ChapterSource(sourcePath, "archive")
            ];
        }

        var archives = Directory
            .EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
            .Where(path => ArchiveExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, NaturalPathComparer.Instance)
            .Select(path => new ChapterSource(Path.GetFullPath(path), "archive"))
            .ToList();

        var imageDirectories = Directory
            .EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories)
            .Prepend(sourcePath)
            .Where(HasDirectImages)
            .OrderBy(path => path, NaturalPathComparer.Instance)
            .Select(path => new ChapterSource(Path.GetFullPath(path), "directory"))
            .ToList();

        var normalizedRoot = Path.GetFullPath(sourcePath);

        // A series folder often contains cover.jpg next to chapter folders/archives.
        // Only treat root-level images as a chapter when the root is the only chapter source.
        if (imageDirectories.Any(x =>
                !string.Equals(x.Path, normalizedRoot, StringComparison.Ordinal)) ||
            archives.Count > 0)
        {
            imageDirectories.RemoveAll(x =>
                string.Equals(x.Path, normalizedRoot, StringComparison.Ordinal));
        }

        // Prefer archives when a folder contains both an archive and extracted copies.
        if (archives.Count > 0)
        {
            var archiveDirectories = archives
                .Select(x => Path.GetDirectoryName(x.Path)!)
                .ToHashSet(StringComparer.Ordinal);

            imageDirectories.RemoveAll(x => archiveDirectories.Contains(x.Path));
        }

        return archives
            .Concat(imageDirectories)
            .OrderBy(x => x.Path, NaturalPathComparer.Instance)
            .ToList();
    }

    private static bool HasDirectImages(string directory)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Any(path => ImageExtensions.Contains(Path.GetExtension(path)));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static DateTime GetSourceUpdatedAt(ChapterSource source)
    {
        if (source.Kind == "archive")
        {
            return File.GetLastWriteTimeUtc(source.Path);
        }

        return Directory
            .EnumerateFiles(source.Path, "*", SearchOption.TopDirectoryOnly)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(Directory.GetLastWriteTimeUtc(source.Path))
            .Max();
    }

    private static async Task<CachedChapter> CacheArchiveAsync(
        string archivePath,
        string cacheDirectory,
        Guid chapterId,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries
            .Where(entry =>
                entry.Length > 0 &&
                ImageExtensions.Contains(Path.GetExtension(entry.Name)))
            .OrderBy(entry => entry.FullName, NaturalPathComparer.Instance)
            .ToArray();

        ValidateArchive(entries);

        var temporary = cacheDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporary);

        try
        {
            var pages = new List<MangaPageItem>(entries.Length);
            var sourceEntries = new List<string>(entries.Length);

            for (var index = 0; index < entries.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = entries[index];
                var extension = NormalizeExtension(Path.GetExtension(entry.Name));
                var cachedPath = Path.Combine(
                    temporary,
                    $"{index:D5}{extension}");

                await using var input = entry.Open();
                await using var output = new FileStream(
                    cachedPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken);

                pages.Add(new MangaPageItem(
                    chapterId,
                    index,
                    Path.Combine(cacheDirectory, Path.GetFileName(cachedPath)),
                    GetMimeType(extension)));
                sourceEntries.Add(entry.FullName);
            }

            ReplaceCacheDirectory(temporary, cacheDirectory);
            return new CachedChapter(pages, sourceEntries);
        }
        catch
        {
            TryDeleteDirectory(temporary);
            throw;
        }
    }

    private static async Task<CachedChapter> CacheDirectoryAsync(
        string sourceDirectory,
        string cacheDirectory,
        Guid chapterId,
        CancellationToken cancellationToken)
    {
        var files = Directory
            .EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, NaturalPathComparer.Instance)
            .ToArray();

        if (files.Length > MaximumPagesPerChapter)
        {
            throw new InvalidOperationException(
                $"Manga chapter contains {files.Length} pages; the limit is {MaximumPagesPerChapter}.");
        }

        var total = files.Sum(path => new FileInfo(path).Length);
        if (files.Any(path => new FileInfo(path).Length > MaximumPageBytes) ||
            total > MaximumChapterBytes)
        {
            throw new InvalidOperationException("Manga chapter exceeds the safe import size limit.");
        }

        var temporary = cacheDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporary);

        try
        {
            var pages = new List<MangaPageItem>(files.Length);
            var sourceEntries = new List<string>(files.Length);

            for (var index = 0; index < files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var source = files[index];
                var extension = NormalizeExtension(Path.GetExtension(source));
                var cachedPath = Path.Combine(
                    temporary,
                    $"{index:D5}{extension}");

                await using var input = new FileStream(
                    source,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(
                    cachedPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken);

                pages.Add(new MangaPageItem(
                    chapterId,
                    index,
                    Path.Combine(cacheDirectory, Path.GetFileName(cachedPath)),
                    GetMimeType(extension)));
                sourceEntries.Add(Path.GetFileName(source));
            }

            ReplaceCacheDirectory(temporary, cacheDirectory);
            return new CachedChapter(pages, sourceEntries);
        }
        catch
        {
            TryDeleteDirectory(temporary);
            throw;
        }
    }

    private static void ValidateArchive(ZipArchiveEntry[] entries)
    {
        if (entries.Length > MaximumPagesPerChapter)
        {
            throw new InvalidOperationException(
                $"Manga archive contains {entries.Length} pages; the limit is {MaximumPagesPerChapter}.");
        }

        long total = 0;
        foreach (var entry in entries)
        {
            if (entry.Length > MaximumPageBytes)
            {
                throw new InvalidOperationException(
                    $"Manga page '{entry.Name}' exceeds the safe page size limit.");
            }

            total += entry.Length;
            if (total > MaximumChapterBytes)
            {
                throw new InvalidOperationException("Manga archive exceeds the safe chapter size limit.");
            }
        }
    }

    private static void ReplaceCacheDirectory(
        string temporary,
        string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        TryDeleteDirectory(destination);
        Directory.Move(temporary, destination);
    }

    private string GetChapterCacheDirectory(
        Guid seriesId,
        Guid chapterId) =>
        Path.Combine(
            cacheRoot,
            seriesId.ToString("N"),
            chapterId.ToString("N"));

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A later import can retry cleanup; never mutate the source manga.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static double? TryParseChapterNumber(string path)
    {
        var name = Directory.Exists(path)
            ? new DirectoryInfo(path).Name
            : Path.GetFileNameWithoutExtension(path);
        var match = ChapterNumberRegex().Match(name);
        if (!match.Success)
        {
            match = LeadingNumberRegex().Match(name);
        }

        return match.Success &&
               double.TryParse(
                   match.Groups["number"].Value,
                   System.Globalization.NumberStyles.AllowDecimalPoint,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var value)
            ? value
            : null;
    }

    private static int? TryParseVolumeNumber(string path)
    {
        foreach (var part in Path.GetFullPath(path)
                     .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     .Reverse())
        {
            var match = VolumeNumberRegex().Match(part);
            if (match.Success &&
                int.TryParse(match.Groups["number"].Value, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string BuildChapterTitle(string path, double number)
    {
        var name = File.Exists(path)
            ? Path.GetFileNameWithoutExtension(path)
            : new DirectoryInfo(path).Name;
        var cleaned = CleanTitle(name);

        return string.IsNullOrWhiteSpace(cleaned)
            ? $"Chapter {number:0.##}"
            : cleaned;
    }

    private static string CleanTitle(string value)
    {
        var cleaned = SeparatorRegex().Replace(value, " ").Trim();
        return WhitespaceRegex().Replace(cleaned, " ");
    }

    private static string NormalizeExtension(string extension)
    {
        var normalized = extension.ToLowerInvariant();
        return normalized == ".jpeg" ? ".jpg" : normalized;
    }

    private static string GetMimeType(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".jpg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".avif" => "image/avif",
            ".gif" => "image/gif",
            _ => "application/octet-stream"
        };

    private static bool IsSameOrBelow(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(parent, StringComparison.Ordinal) ||
               full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
               full.StartsWith(parent + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private sealed record ChapterSource(string Path, string Kind);
    private sealed record CachedChapter(
        IReadOnlyList<MangaPageItem> Pages,
        IReadOnlyList<string> SourceEntries);

    private sealed class NaturalPathComparer : IComparer<string>
    {
        public static NaturalPathComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var left = NaturalPartsRegex().Matches(x);
            var right = NaturalPartsRegex().Matches(y);
            var count = Math.Min(left.Count, right.Count);

            for (var i = 0; i < count; i++)
            {
                var a = left[i].Value;
                var b = right[i].Value;

                if (long.TryParse(a, out var an) && long.TryParse(b, out var bn))
                {
                    var numeric = an.CompareTo(bn);
                    if (numeric != 0) return numeric;
                    continue;
                }

                var text = StringComparer.OrdinalIgnoreCase.Compare(a, b);
                if (text != 0) return text;
            }

            return left.Count != right.Count
                ? left.Count.CompareTo(right.Count)
                : StringComparer.OrdinalIgnoreCase.Compare(x, y);
        }
    }

    [GeneratedRegex(@"(?:chapter|chap|ch|c)[\s._-]*(?<number>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChapterNumberRegex();

    [GeneratedRegex(@"^\D*(?<number>\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingNumberRegex();

    [GeneratedRegex(@"(?:volume|vol|v)[\s._-]*(?<number>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumeNumberRegex();

    [GeneratedRegex(@"[._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\d+|\D+", RegexOptions.CultureInvariant)]
    private static partial Regex NaturalPartsRegex();
}
