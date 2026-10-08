using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Jularr.Web.Features.Books;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Novels;

/// <summary>Result of importing one EPUB file.</summary>
public sealed record NovelEpubImportOutcome(
    string FileName,
    bool Succeeded,
    Guid? WorkId,
    int? VolumeNumber,
    string Message)
{
    public static NovelEpubImportOutcome Failed(string fileName, string message) =>
        new(fileName, false, null, null, message);

    /// <summary>One status line for a batch: successes counted, failures named.</summary>
    public static string Summarize(IReadOnlyList<NovelEpubImportOutcome> outcomes)
    {
        if (outcomes.Count == 0)
        {
            return "No EPUB files were found.";
        }

        if (outcomes.Count == 1)
        {
            var single = outcomes[0];
            return single.Succeeded
                ? single.Message
                : $"{single.FileName}: {single.Message}";
        }

        var succeeded = outcomes.Count(x => x.Succeeded);
        var failures = outcomes
            .Where(x => !x.Succeeded)
            .Select(x => $"{x.FileName}: {x.Message}");

        return string.Join(
            " ",
            new[] { $"{succeeded} of {outcomes.Count} EPUB volumes imported." }
                .Concat(failures));
    }
}

/// <summary>
/// Result of importing one completed download. <see cref="RejectedBecause"/> is set when the
/// package was refused before anything was stored.
/// </summary>
public sealed record NovelEpubDownloadImport(
    IReadOnlyList<NovelEpubImportOutcome> Outcomes,
    string? RejectedBecause);

/// <summary>Runs an owner EPUB upload through Operations.</summary>
public static class NovelEpubUploads
{
    /// <summary>Returns null when no or too many files were posted.</summary>
    public static async Task<IReadOnlyList<NovelEpubImportOutcome>?> ImportAsync(
        NovelEpubImportService imports,
        Operations.OperationRunner operations,
        string profileId,
        IReadOnlyList<IFormFile>? files,
        Guid? targetWorkId,
        CancellationToken cancellationToken)
    {
        var uploads = files?.Where(x => x.Length > 0).ToArray() ?? [];
        if (uploads.Length is 0 or > NovelEpubUploadRequestLimitsAttribute.MaximumFiles)
        {
            return null;
        }

        return await operations.RunAsync(
            new Operations.OperationDescriptor(
                "novel-epub-upload-import",
                "Novels",
                "Import uploaded EPUB volumes",
                uploads.Length == 1 ? uploads[0].FileName : $"{uploads.Length} files",
                profileId,
                Operations.OperationLane.Normal,
                Retryable: false),
            async (operation, token) =>
            {
                await operation.ReportAsync(
                    10,
                    "Parsing uploaded EPUB volumes.",
                    cancellationToken: token);

                var streams = uploads
                    .Select(file => (Stream: file.OpenReadStream(), file.FileName))
                    .ToArray();
                try
                {
                    return await imports.ImportUploadsAsync(streams, targetWorkId, token);
                }
                finally
                {
                    foreach (var (stream, _) in streams)
                    {
                        await stream.DisposeAsync();
                    }
                }
            },
            "EPUB volume upload processed.",
            cancellationToken);
    }
}

/// <summary>
/// The one import path for user-provided EPUB light-novel volumes, used by
/// uploads, completed downloads and the Light Novel inbox folder. It parses with the shared
/// <see cref="EpubBookParser"/>, groups volumes into a series, caches
/// normalized assets under Jularr data and writes chapters through
/// <see cref="NovelVolumeContent"/>. Source files are only ever read.
/// Every file succeeds or fails on its own with a diagnostic.
/// </summary>
public sealed partial class NovelEpubImportService(
    AppDbContext db,
    NovelVolumeAssetStore assets,
    ILogger<NovelEpubImportService> logger,
    NovelMetadataService? metadata = null)
{
    public const string Provider = "epub";

    private const long MaxEpubBytes = 100L * 1024 * 1024;
    private const int MaxInboxFiles = 500;

    /// <summary>
    /// Imports uploaded EPUB volumes. With <paramref name="targetWorkId"/>
    /// every file becomes a volume of that series; otherwise the series is
    /// resolved from EPUB metadata.
    /// </summary>
    public async Task<IReadOnlyList<NovelEpubImportOutcome>> ImportUploadsAsync(
        IReadOnlyList<(Stream Stream, string FileName)> files,
        Guid? targetWorkId,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<NovelEpubImportOutcome>(files.Count);
        foreach (var (stream, fileName) in files)
        {
            outcomes.Add(await ImportFileAsync(
                stream,
                fileName,
                targetWorkId,
                seriesHint: null,
                cancellationToken));
        }

        await AutoMatchAsync(outcomes, cancellationToken);
        return outcomes;
    }

    /// <summary>
    /// Imports every EPUB from the Light Novel inbox folder. Files directly
    /// in the directory resolve their series from metadata; files in a
    /// subfolder belong to the series named by that folder. Unchanged files
    /// are skipped.
    /// </summary>
    public async Task<IReadOnlyList<NovelEpubImportOutcome>> ImportDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        var folder = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(folder))
        {
            throw new InvalidOperationException(
                $"Light-novel import directory '{folder}' does not exist.");
        }

        var files = Directory
            .EnumerateFiles(folder, "*.epub", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, SeriesHint: (string?)null))
            .Concat(Directory
                .EnumerateDirectories(folder)
                .SelectMany(directory => Directory
                    .EnumerateFiles(directory, "*.epub", SearchOption.TopDirectoryOnly)
                    .Select(path => (Path: path, SeriesHint: (string?)Path.GetFileName(directory)))))
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxInboxFiles)
            .ToArray();

        var outcomes = new List<NovelEpubImportOutcome>(files.Length);
        foreach (var (path, seriesHint) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);

            FileStream stream;
            try
            {
                stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 81920,
                    useAsync: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                outcomes.Add(NovelEpubImportOutcome.Failed(
                    fileName,
                    $"File could not be opened (it may still be copying): {exception.Message}"));
                continue;
            }

            await using (stream)
            {
                outcomes.Add(await ImportFileAsync(
                    stream,
                    fileName,
                    targetWorkId: null,
                    seriesHint,
                    cancellationToken));
            }
        }

        await AutoMatchAsync(outcomes, cancellationToken);
        return outcomes;
    }

    /// <summary>
    /// Imports one completed download (an EPUB file or a job folder) as volumes of one series.
    /// Unlike the inbox, folder names are no series hints: the series comes from each EPUB's
    /// metadata, and EPUBs are found at any depth. Every file is parsed first; a package that
    /// holds no importable EPUB, or EPUBs of more than one series, is rejected before anything
    /// is stored.
    /// </summary>
    public async Task<NovelEpubDownloadImport> ImportDownloadAsync(
        string sourcePath,
        CancellationToken cancellationToken,
        bool recordSourceStoragePath = false,
        Guid? targetWorkId = null)
    {
        var source = Path.GetFullPath(sourcePath);
        string[] paths;
        if (File.Exists(source))
        {
            paths = [source];
        }
        else if (Directory.Exists(source))
        {
            paths = Directory
                .EnumerateFiles(source, "*.epub", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxInboxFiles)
                .ToArray();
        }
        else
        {
            throw new IOException($"The completed download '{source}' does not exist.");
        }

        var outcomes = new List<NovelEpubImportOutcome>();
        var valid = new List<string>();
        var seriesKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            if (!fileName.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
            {
                outcomes.Add(NovelEpubImportOutcome.Failed(fileName, "Only .epub files can be imported."));
                continue;
            }

            try
            {
                await using var stream = OpenShared(path);
                var bytes = await ReadBoundedAsync(stream, cancellationToken);
                using var memory = new MemoryStream(bytes, writable: false);
                var parsed = EpubBookParser.Parse(memory, fileName, includeAssets: false);
                seriesKeys.Add(SeriesKey(FirstNonEmpty(
                    parsed.SeriesTitle,
                    StripVolumeMarker(parsed.Title),
                    Path.GetFileNameWithoutExtension(fileName))!));
                valid.Add(path);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                outcomes.Add(NovelEpubImportOutcome.Failed(fileName, exception.Message));
            }
        }

        if (valid.Count == 0)
        {
            return new NovelEpubDownloadImport(
                outcomes,
                paths.Length == 0
                    ? "The download contains no EPUB files."
                    : $"The download contains no usable EPUB: {NovelEpubImportOutcome.Summarize(outcomes)}");
        }

        if (seriesKeys.Count > 1)
        {
            return new NovelEpubDownloadImport(
                outcomes,
                $"The download contains EPUBs of {seriesKeys.Count} different series.");
        }

        foreach (var path in valid)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = OpenShared(path);
            outcomes.Add(await ImportFileAsync(
                stream,
                Path.GetFileName(path),
                targetWorkId: targetWorkId,
                seriesHint: null,
                cancellationToken,
                recordSourceStoragePath ? Path.GetFullPath(path) : null));
        }

        await AutoMatchAsync(outcomes, cancellationToken);
        return new NovelEpubDownloadImport(outcomes, null);
    }

    private static FileStream OpenShared(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            useAsync: true);

    /// <summary>
    /// Removes one EPUB volume with its chapters (and their notes) and cached
    /// assets, then closes the numbering gap.
    /// </summary>
    public async Task RemoveVolumeAsync(
        Guid workId,
        Guid volumeId,
        CancellationToken cancellationToken)
    {
        var volume = await db.NovelVolumes
            .SingleOrDefaultAsync(
                x => x.Id == volumeId && x.WorkId == workId && x.Kind == NovelVolumeKinds.Epub,
                cancellationToken)
            ?? throw new InvalidOperationException("EPUB volume was not found.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.NovelVolumes.Remove(volume);
        await db.SaveChangesAsync(cancellationToken);
        await NovelVolumeContent.RenumberSeriesAsync(db, workId, null, [], cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        assets.DeleteVolume(volumeId);
    }

    private async Task<NovelEpubImportOutcome> ImportFileAsync(
        Stream stream,
        string fileName,
        Guid? targetWorkId,
        string? seriesHint,
        CancellationToken cancellationToken,
        string? sourceStoragePath = null)
    {
        fileName = Path.GetFileName(fileName);
        if (!fileName.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        {
            return NovelEpubImportOutcome.Failed(fileName, "Only .epub files can be imported.");
        }

        byte[] bytes;
        ParsedEpubBook parsed;
        try
        {
            bytes = await ReadBoundedAsync(stream, cancellationToken);
            using var memory = new MemoryStream(bytes, writable: false);
            parsed = EpubBookParser.Parse(memory, fileName, includeAssets: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException)
        {
            logger.LogWarning(
                "EPUB volume {FileName} could not be imported: {Reason}",
                fileName,
                exception.Message);
            return NovelEpubImportOutcome.Failed(fileName, exception.Message);
        }

        var contentHash = Convert.ToHexString(SHA256.HashData(bytes));
        NovelVolume? volume = null;
        var createdVolume = false;

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var work = await ResolveSeriesAsync(parsed, fileName, targetWorkId, seriesHint, cancellationToken);
            (volume, createdVolume) = await ResolveVolumeAsync(work, parsed, fileName, cancellationToken);
            var storagePath = TruncateOptional(sourceStoragePath, 2048);

            if (!createdVolume && volume.SourceContentHash == contentHash)
            {
                if (storagePath is not null && !string.Equals(volume.SourceStoragePath, storagePath, StringComparison.Ordinal))
                {
                    volume.SourceStoragePath = storagePath;
                    volume.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                else
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                db.ChangeTracker.Clear();
                return new NovelEpubImportOutcome(
                    fileName,
                    true,
                    work.Id,
                    volume.Number,
                    $"Volume {volume.Number} of {work.Title} is already up to date.");
            }

            volume.Title = Truncate(parsed.Title, 500);
            volume.SourceFileName = Truncate(fileName, 500);
            if (storagePath is not null)
            {
                volume.SourceStoragePath = storagePath;
            }
            volume.SourceContentHash = contentHash;
            volume.UpdatedAt = DateTime.UtcNow;

            var assetNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in parsed.Assets)
            {
                var name = await assets.SaveAsync(volume.Id, asset.Bytes, asset.MediaType, cancellationToken);
                if (name is not null)
                {
                    assetNames[asset.Path] = name;
                }
            }

            volume.CoverAsset = parsed.CoverBytes is { Length: > 0 } cover &&
                parsed.CoverMediaType is { } coverType
                    ? await assets.SaveAsync(volume.Id, cover, coverType, cancellationToken)
                    : null;

            var chapters = parsed.Chapters
                .Select(chapter => new NovelVolumeChapterInput(
                    "epub:" + (chapter.SourcePath ?? chapter.Number.ToString(CultureInfo.InvariantCulture)),
                    chapter.Title,
                    chapter.Text,
                    NovelChapterDocument.Serialize(ResolveImages(chapter.Blocks, assetNames)),
                    chapter.GroupTitle))
                .ToArray();

            var result = await NovelVolumeContent.SyncChaptersAsync(
                db,
                work,
                volume,
                chapters,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            var keep = assetNames.Values.ToHashSet(StringComparer.Ordinal);
            if (volume.CoverAsset is not null)
            {
                keep.Add(volume.CoverAsset);
            }

            assets.Prune(volume.Id, keep);
            db.ChangeTracker.Clear();

            return new NovelEpubImportOutcome(
                fileName,
                true,
                work.Id,
                volume.Number,
                $"Volume {volume.Number} of {work.Title}: {result.Added} new, {result.Updated} updated, " +
                $"{result.Removed} removed, {result.Unchanged} unchanged chapters.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or DbUpdateException or IOException)
        {
            db.ChangeTracker.Clear();
            if (createdVolume && volume is not null)
            {
                assets.DeleteVolume(volume.Id);
            }

            logger.LogWarning(
                exception,
                "EPUB volume {FileName} could not be stored.",
                fileName);
            return NovelEpubImportOutcome.Failed(fileName, exception.Message);
        }
    }

    private async Task<NovelWork> ResolveSeriesAsync(
        ParsedEpubBook parsed,
        string fileName,
        Guid? targetWorkId,
        string? seriesHint,
        CancellationToken cancellationToken)
    {
        if (targetWorkId is Guid workId)
        {
            var target = await db.NovelWorks.SingleOrDefaultAsync(x => x.Id == workId, cancellationToken)
                ?? throw new InvalidOperationException("The target light-novel series was not found.");

            return target.SourceProvider == Provider
                ? target
                : throw new InvalidOperationException(
                    "EPUB volumes can only be added to an EPUB light-novel series.");
        }

        var seriesTitle = FirstNonEmpty(
            seriesHint,
            parsed.SeriesTitle,
            StripVolumeMarker(parsed.Title),
            Path.GetFileNameWithoutExtension(fileName))!;
        var sourceKey = SeriesKey(seriesTitle);

        var work = await db.NovelWorks.SingleOrDefaultAsync(
            x => x.SourceProvider == Provider && x.SourceKey == sourceKey,
            cancellationToken);

        if (work is not null)
        {
            return work;
        }

        work = new NovelWork
        {
            SourceProvider = Provider,
            SourceKey = sourceKey,
            SourceUrl = "epub-series:" + sourceKey,
            Title = Truncate(seriesTitle, 500),
            Author = TruncateOptional(parsed.Author, 300),
            Description = TruncateOptional(parsed.Description, 4000),
            Format = "LIGHT_NOVEL",
            ImportedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.NovelWorks.Add(work);
        return work;
    }

    /// <summary>
    /// Resolves the volume a file refreshes: same source identity first, then
    /// the same volume number (a replacement file), otherwise a new volume.
    /// </summary>
    private async Task<(NovelVolume Volume, bool Created)> ResolveVolumeAsync(
        NovelWork work,
        ParsedEpubBook parsed,
        string fileName,
        CancellationToken cancellationToken)
    {
        var sourceKey = VolumeKey(parsed);
        var number = VolumeNumberFrom(parsed.SeriesIndex)
            ?? ParseVolumeNumber(parsed.Title)
            ?? ParseVolumeNumber(Path.GetFileNameWithoutExtension(fileName));

        var volumes = db.Entry(work).State == EntityState.Added
            ? []
            : await db.NovelVolumes
                .Where(x => x.WorkId == work.Id)
                .ToListAsync(cancellationToken);

        var existing = volumes.FirstOrDefault(x => x.SourceKey == sourceKey)
            ?? (number is int slot
                ? volumes.FirstOrDefault(x => x.Number == slot && x.Kind == NovelVolumeKinds.Epub)
                : null);

        if (existing is not null)
        {
            existing.SourceKey = sourceKey;
            return (existing, false);
        }

        if (number is int requested && volumes.Any(x => x.Number == requested))
        {
            throw new InvalidOperationException(
                $"Volume {requested} of this series is not an EPUB volume and cannot be replaced.");
        }

        var volume = new NovelVolume
        {
            WorkId = work.Id,
            Number = number ?? (volumes.Count == 0 ? 1 : volumes.Max(x => x.Number) + 1),
            Kind = NovelVolumeKinds.Epub,
            SourceKey = sourceKey,
            ImportedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.NovelVolumes.Add(volume);
        return (volume, true);
    }

    private static IReadOnlyList<NovelContentBlock> ResolveImages(
        IReadOnlyList<NovelContentBlock> blocks,
        IReadOnlyDictionary<string, string> assetNames) =>
        blocks
            .Select(block => block.Kind != NovelContentBlock.ImageKind
                ? block
                : block.Source is not null && assetNames.TryGetValue(block.Source, out var asset)
                    ? block with { Source = asset }
                    : null)
            .Where(block => block is not null)
            .Cast<NovelContentBlock>()
            .ToArray();

    private async Task AutoMatchAsync(
        IEnumerable<NovelEpubImportOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        if (metadata is null)
        {
            return;
        }

        foreach (var workId in outcomes
                     .Where(x => x.Succeeded && x.WorkId is not null)
                     .Select(x => x.WorkId!.Value)
                     .Distinct())
        {
            // Canonical AniList matching and segment reconciliation, once per
            // series and batch.
            await metadata.AutoMatchAsync(workId, cancellationToken);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxEpubBytes)
            {
                throw new InvalidOperationException("EPUB exceeds the 100 MB import limit.");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    public static string SeriesKey(string seriesTitle)
    {
        var normalized = new StringBuilder();
        foreach (var character in seriesTitle.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                normalized.Append(character);
            }
        }

        var canonical = normalized.Length > 0 ? normalized.ToString() : seriesTitle.Trim();
        return "series-" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..40]
            .ToLowerInvariant();
    }

    /// <summary>
    /// Deterministic volume identity: the package unique identifier, else the
    /// ISBN, else title and author.
    /// </summary>
    public static string VolumeKey(ParsedEpubBook parsed)
    {
        var identity = FirstNonEmpty(
            parsed.UniqueIdentifier is null ? null : "id:" + parsed.UniqueIdentifier.Trim().ToLowerInvariant(),
            parsed.Isbn13 is null ? null : "isbn:" + parsed.Isbn13,
            parsed.Isbn10 is null ? null : "isbn:" + parsed.Isbn10)
            ?? "title:" + parsed.Title.Trim().ToLowerInvariant() + "|" + parsed.Author?.Trim().ToLowerInvariant();

        return identity.Length <= 120
            ? identity
            : identity[..identity.IndexOf(':')] + ":sha256-" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..40].ToLowerInvariant();
    }

    private static int? VolumeNumberFrom(decimal? seriesIndex) =>
        seriesIndex is decimal index && index == decimal.Truncate(index) && index is >= 1 and <= 9999
            ? (int)index
            : null;

    /// <summary>Reads a volume number from a title such as "第3巻", "Vol. 3" or "(3)".</summary>
    public static int? ParseVolumeNumber(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var match = VolumeMarker().Match(title.Normalize(NormalizationForm.FormKC));
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["n"].Value;
        var number = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : ParseKanjiNumber(value);

        return number is >= 1 and <= 9999 ? number : null;
    }

    /// <summary>The series part of a volume title ("Series Vol. 3: Subtitle" → "Series").</summary>
    public static string? StripVolumeMarker(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var normalized = title.Normalize(NormalizationForm.FormKC);
        var match = VolumeMarker().Match(normalized);
        if (!match.Success || match.Index == 0)
        {
            return title.Trim();
        }

        var series = normalized[..match.Index].Trim().TrimEnd(':', '-', '―', '—', '–', '・', ',', '(', '[', '（', '【', '〈', '《', ' ');
        return series.Length > 0 ? series : title.Trim();
    }

    private static int? ParseKanjiNumber(string value)
    {
        const string digits = "〇一二三四五六七八九";
        var total = 0;
        var current = 0;
        foreach (var character in value)
        {
            var digit = digits.IndexOf(character);
            if (digit >= 0)
            {
                current = current * 10 + digit;
                continue;
            }

            var unit = character switch
            {
                '十' => 10,
                '百' => 100,
                _ => 0
            };

            if (unit == 0)
            {
                return null;
            }

            total += (current == 0 ? 1 : current) * unit;
            current = 0;
        }

        total += current;
        return total > 0 ? total : null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string? TruncateOptional(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : Truncate(value.Trim(), maxLength);

    [GeneratedRegex(
        @"(?:第\s*(?<n>[0-9〇一二三四五六七八九十百]+)\s*[巻卷部集]|(?<n>[0-9]+)\s*[巻卷]|\b(?:vol(?:ume)?|band|bd|tome|book)\.?\s*(?<n>[0-9]+)|[(\[（【〈《]\s*(?<n>[0-9]+)\s*[)\]）】〉》]|\s(?<n>[0-9]{1,3})\s*$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumeMarker();
}
