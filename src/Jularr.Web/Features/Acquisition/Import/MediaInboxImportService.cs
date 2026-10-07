using System.Collections.Concurrent;
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

    private static readonly ConcurrentDictionary<MediaAcquisitionKind, byte> RunningScans = new();

    public static readonly MediaAcquisitionKind[] InboxKinds =
    [
        MediaAcquisitionKind.Manga,
        MediaAcquisitionKind.LightNovel,
        MediaAcquisitionKind.Book,
        MediaAcquisitionKind.Movie,
        MediaAcquisitionKind.Tv,
        MediaAcquisitionKind.Music
    ];

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

        // An inbox that is, contains or sits inside any LibraryRoot would import library files onto themselves (or another type's library).
        if (await routing.FindOverlappingRootAsync(root, cancellationToken) is { } overlapping)
        {
            throw new InvalidOperationException($"The {Label(kind)} inbox overlaps the library root '{overlapping.Name}'. Choose an inbox outside every library root.");
        }

        // An older layout keeps the Light Novel inbox inside the Books inbox; a scan never
        // imports another media type's inbox.
        var excluded = InboxKinds
            .Where(other => other != kind)
            .Select(state.InboxFor)
            .OfType<string>()
            .Select(Path.GetFullPath)
            .Where(other => StoragePaths.IsBelow(other, root))
            .ToArray();

        // One scan per media type at a time: a second click while the first still imports would race it for the same files.
        if (!RunningScans.TryAdd(kind, 0))
        {
            throw new InvalidOperationException($"A {Label(kind)} inbox scan is already running.");
        }

        try
        {
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
        finally
        {
            RunningScans.TryRemove(kind, out _);
        }
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
        MediaAcquisitionKind.Music => "Music",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
