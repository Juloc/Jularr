using Jularr.Web.Features.Library;
using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Storage;

namespace Jularr.Web.Features.Audiobooks;

/// <summary>
/// The audiobook importer behind the shared completed-download dispatcher and the audiobook inbox scan
/// (#440). A completed download's audio files (a single M4B, or the MP3 parts of a chaptered audiobook)
/// are placed into the audiobook library as <c>Author/Title (Year)/…</c> and recorded as one
/// <see cref="Audiobook"/> with its <see cref="AudiobookFile"/> rows, bridged to the universal media core
/// as a Book work with an <c>audiobook</c> edition/version. When no audiobook library folder is configured
/// the files are imported in place. Metadata comes from the acquisition request when there is one,
/// otherwise from the release name via the shared scene parser (never a filename-only provider match).
/// </summary>
public sealed partial class AudiobookCompletedDownloadImportAdapter(
    AudiobookLibraryService audiobooks,
    MediaAcquisitionRegistry registry,
    AnimeImportSettingsStore importSettings,
    IHardLinkCreator hardLinks,
    ILogger<AudiobookCompletedDownloadImportAdapter> logger,
    LibraryRootRoutingService? routing = null)
    : ICompletedDownloadImportAdapter, IMediaInboxImportAdapter
{
    /// <summary>Why a finished download did not become an audiobook; the next release is tried.</summary>
    public const string NoAudioFileReason = "The download contained no audiobook file.";

    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Audiobook;

    [GeneratedRegex(@"(?<year>(?:19|20)\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex YearRegex();

    public async Task<CompletedDownloadImportResult> ImportAsync(
        CompletedDownloadImportRequest request,
        CancellationToken cancellationToken)
    {
        var files = CompletedDownloadFiles.Enumerate(request.SourcePath, out var error);
        if (error is not null)
        {
            // Not the release's fault: an unmapped path or an offline share. Wait rather than burn releases.
            return CompletedDownloadImportResult.RetryLater(error);
        }

        var audio = files
            .Where(file => AudiobookFileFormats.IsSupported(file.Path))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();
        if (audio.Count == 0)
        {
            return CompletedDownloadImportResult.RejectRelease(NoAudioFileReason);
        }

        // A single-file audiobook carries its metadata in the file name; a multi-file (chaptered) one in
        // the release folder name.
        var nameHint = audio.Count == 1
            ? Path.GetFileNameWithoutExtension(audio[0].Path)
            : LeafName(request.SourcePath);
        var metadata = ResolveMetadata(request, nameHint);

        try
        {
            var settings = await importSettings.LoadAsync(cancellationToken);
            if (routing is not null)
            {
                settings = await routing.WithRoutedLibrariesAsync(settings, cancellationToken);
            }

            var library = settings.LibraryFor(MediaAcquisitionKind.Audiobook);
            if (library is null)
            {
                return CompletedDownloadImportResult.RetryLater(LibraryRootRoutingService.MissingDefaultMessage(LibraryContentType.Audiobook));
            }

            var recorded = new List<AudiobookFileInput>();
            var mode = settings.ModeFor(MediaAcquisitionKind.Audiobook);
            var (action, allowFallback) = ImportFileTransfer.Resolve(mode);
            var folder = Path.Combine(
                library.LibraryRoot!, AudiobookNaming.FolderPath(metadata.Author, metadata.Title, metadata.Year));
            var placer = new LibraryFilePlacer(new ImportFileTransfer(hardLinks));

            await request.ReportProgressAsync(CompletedDownloadImportPhase.Importing, "Placing the audiobook in the library.");
            foreach (var file in audio)
            {
                var leafName = audio.Count == 1
                    ? AudiobookNaming.SingleFileName(metadata.Title, metadata.Year, Path.GetExtension(file.Path))
                    : Path.GetFileName(file.Path);
                var destination = Path.Combine(folder, leafName);

                // Idempotent: a file already placed here is left as is (re-import only refreshes the record).
                if (LibraryFilePlacer.FindDestinationConflict(destination, []) is null)
                {
                    placer.Place(new LibraryFilePlacement(file.Path, destination, action, allowFallback, [], []));
                }

                recorded.Add(ToFileInput(destination, file.SizeBytes));
            }

            var libraryPath = folder;
            var placement = new CompletedDownloadPlacement(folder, mode);

            var entry = await audiobooks.EnsureAsync(
                metadata.Title, metadata.Year, metadata.Author, metadata.Narrator, metadata.Asin,
                durationMs: null, chapterCount: audio.Count > 1 ? audio.Count : null,
                libraryPath, recorded, cancellationToken);
            return CompletedDownloadImportResult.Completed(
                $"Imported audiobook \"{entry.Audiobook.Title}\".", resultUrl: null, placement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CrossDeviceLinkException)
        {
            logger.LogWarning(exception, "Audiobook import is waiting for storage for '{SourcePath}'.", request.SourcePath);
            return CompletedDownloadImportResult.RetryLater("The audiobook import is waiting for storage.");
        }
    }

    /// <summary>
    /// Each top-level entry directly in the inbox is one audiobook: a folder of MP3 parts is one chaptered
    /// audiobook named after the folder, a loose M4B/MP3 file is a single-file audiobook. Files are read
    /// in place. Other media types' inbox folders nested here are skipped.
    /// </summary>
    public async Task<MediaInboxImportResult> ImportInboxAsync(
        string inboxRoot,
        IReadOnlyCollection<string> excludedFolders,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(inboxRoot))
        {
            return new MediaInboxImportResult(0, $"The audiobook inbox folder does not exist: {inboxRoot}");
        }

        var imported = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(inboxRoot).OrderBy(x => x, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsExcluded(entry, excludedFolders))
            {
                continue;
            }

            var audio = CompletedDownloadFiles.Enumerate(entry, out var error)
                .Where(file => AudiobookFileFormats.IsSupported(file.Path))
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .ToList();
            if (error is not null || audio.Count == 0)
            {
                continue;
            }

            var nameHint = Directory.Exists(entry)
                ? Path.GetFileName(entry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : Path.GetFileNameWithoutExtension(entry);
            var metadata = ResolveMetadata(request: null, nameHint);

            await audiobooks.EnsureAsync(
                metadata.Title, metadata.Year, metadata.Author, metadata.Narrator, metadata.Asin,
                durationMs: null, chapterCount: audio.Count > 1 ? audio.Count : null,
                Directory.Exists(entry) ? entry : Path.GetDirectoryName(entry),
                audio.Select(file => ToFileInput(file.Path, file.SizeBytes)).ToList(),
                cancellationToken);
            imported++;
        }

        return new MediaInboxImportResult(
            imported,
            imported == 0 ? "No new audiobooks in the inbox." : $"Imported {imported} audiobook(s) from the inbox.");
    }

    private AudiobookMetadata ResolveMetadata(CompletedDownloadImportRequest? request, string nameHint)
    {
        var name = string.IsNullOrWhiteSpace(nameHint) ? "Untitled" : nameHint;
        var release = registry.ParserFor(MediaAcquisitionKind.Audiobook).TryParse(name, out var parsed) ? parsed : null;

        if (request?.Request is { } acquisition)
        {
            var requestedYear = TryParseYear(acquisition.Title) ?? release?.AirDate?.Year ?? TryParseYear(name);
            var asin = string.Equals(acquisition.Provider, "audible", StringComparison.OrdinalIgnoreCase)
                ? acquisition.ExternalId
                : null;
            // For an audiobook request the subtitle carries the author (the requester's chosen author line).
            return new AudiobookMetadata(acquisition.Title, requestedYear, acquisition.Subtitle, Narrator: null, asin);
        }

        var title = release?.SeriesTitle is { Length: > 0 } seriesTitle ? seriesTitle : name;
        return new AudiobookMetadata(title, release?.AirDate?.Year ?? TryParseYear(name), Author: null, Narrator: null, Asin: null);
    }

    private static string LeafName(string path) =>
        File.Exists(path)
            ? Path.GetFileNameWithoutExtension(path)
            : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static AudiobookFileInput ToFileInput(string path, long sizeBytes) =>
        new(
            Path.GetFileName(path),
            AudiobookFileFormats.FromPath(path) ?? Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
            path,
            sizeBytes);

    private static int? TryParseYear(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = YearRegex().Match(text);
        return match.Success ? int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture) : null;
    }

    private static bool IsExcluded(string path, IReadOnlyCollection<string> excludedFolders) =>
        excludedFolders.Any(folder =>
            !string.IsNullOrWhiteSpace(folder) &&
            path.Replace('\\', '/').TrimEnd('/')
                .Equals(folder.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    private readonly record struct AudiobookMetadata(
        string Title,
        int? Year,
        string? Author,
        string? Narrator,
        string? Asin);
}
