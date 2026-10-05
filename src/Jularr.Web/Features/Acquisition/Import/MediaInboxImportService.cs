using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Storage;

namespace Jularr.Web.Features.Acquisition.Import;

/// <summary>
/// Scans the configured inbox folder of one media type (Settings → Acquisition → Media folders)
/// with that media type's completed-download importer, for content Jularr did not download: a
/// manual copy, a file from another tool. It runs as one Operation when the owner asks for it.
/// Imports are idempotent: an unchanged file is recognized and not imported twice.
/// </summary>
public sealed class MediaInboxImportService(
    AnimeImportSettingsStore settings,
    IEnumerable<IMediaInboxImportAdapter> adapters,
    OperationRunner operations,
    LibraryRootRoutingService routing,
    IInstanceModuleService? instanceModules = null)
{
    public const string OperationKind = "media-inbox-import";

    public static readonly MediaAcquisitionKind[] InboxKinds =
    [
        MediaAcquisitionKind.Manga,
        MediaAcquisitionKind.LightNovel,
        MediaAcquisitionKind.Book,
        MediaAcquisitionKind.Movie,
        MediaAcquisitionKind.Tv
    ];

    /// <summary>The LibraryRoot content type whose default root receives this media type's imports, or null while its importer still reads a per-media library folder.</summary>
    public static LibraryContentType? RoutedContentType(MediaAcquisitionKind kind) => kind switch
    {
        MediaAcquisitionKind.Movie => LibraryContentType.Movie,
        MediaAcquisitionKind.Tv => LibraryContentType.Tv,
        _ => null
    };

    public async Task<string?> InboxAsync(
        MediaAcquisitionKind kind,
        CancellationToken cancellationToken) =>
        (await settings.LoadAsync(cancellationToken)).InboxFor(kind);

    public async Task<MediaInboxImportResult> RunAsync(
        MediaAcquisitionKind kind,
        string? profileId,
        CancellationToken cancellationToken)
    {
        if (instanceModules is not null)
        {
            var instance = await instanceModules.GetAsync(cancellationToken);
            if (!instance.IsEnabled(InstanceModule.Acquisition)
                || !instance.IsEnabled(AcquisitionInstanceModules.For(kind)))
            {
                throw new InvalidOperationException(
                    "Inbox import is disabled for this media type.");
            }
        }

        var adapter = adapters.FirstOrDefault(candidate => candidate.Kind == kind)
            ?? throw new InvalidOperationException($"{Label(kind)} has no inbox import.");
        var state = await settings.LoadAsync(cancellationToken);
        var configured = state.InboxFor(kind)
            ?? throw new InvalidOperationException(
                $"No {Label(kind)} inbox folder is configured. Set one under Settings → Acquisition → Media folders.");
        var root = Path.GetFullPath(configured);
        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException($"The {Label(kind)} inbox '{root}' is not available.");
        }

        // Scanning an inbox that is, contains or sits inside the destination root would import library files onto themselves.
        if (RoutedContentType(kind) is { } contentType && await routing.ResolveDefaultAsync(contentType, cancellationToken) is { } route && Overlaps(root, route.Path))
        {
            throw new InvalidOperationException($"The {Label(kind)} inbox overlaps the {Label(kind)} library root. Choose an inbox outside the library.");
        }

        // An older layout keeps the Light Novel inbox inside the Books inbox; a scan never
        // imports another media type's inbox.
        var excluded = InboxKinds
            .Where(other => other != kind)
            .Select(state.InboxFor)
            .OfType<string>()
            .Select(Path.GetFullPath)
            .Where(other => IsBelow(other, root))
            .ToArray();

        return await operations.RunAsync(
            new OperationDescriptor(
                OperationKind,
                Label(kind),
                $"Import {Label(kind)} inbox",
                root,
                profileId,
                OperationLane.Normal,
                Retryable: false),
            async (operation, token) =>
            {
                await operation.ReportAsync(10, $"Scanning {root}.", cancellationToken: token);
                var result = await adapter.ImportInboxAsync(root, excluded, token);
                await operation.LogAsync(OperationLogLevel.Information, "Import", result.Message, token);
                return result;
            },
            "Inbox scan completed.",
            cancellationToken);
    }

    public static string Label(MediaAcquisitionKind kind) => kind switch
    {
        MediaAcquisitionKind.Manga => "Manga",
        MediaAcquisitionKind.LightNovel => "Light Novels",
        MediaAcquisitionKind.Book => "Books",
        MediaAcquisitionKind.Anime => "Anime",
        MediaAcquisitionKind.Movie => "Movies",
        MediaAcquisitionKind.Tv => "TV",
        MediaAcquisitionKind.Audiobook => "Audiobooks",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Two folders are the same or one lies inside the other.</summary>
    public static bool Overlaps(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.Ordinal) ||
        IsBelow(left, right) ||
        IsBelow(right, left);

    public static bool IsBelow(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
               full.StartsWith(parent + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
