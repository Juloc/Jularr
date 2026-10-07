using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Library;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Storage;

/// <summary>Owns specialized LibraryRoot content routing and the effective placement policy returned to importers.</summary>
public sealed class LibraryRootRoutingService(AppDbContext db)
{
    /// <summary>
    /// The content types whose importers resolve their final destination and placement through this service, and so every content type the Admin
    /// Storage page lets the owner route. A type without a default root imports nothing: its imports wait until the owner chooses one.
    /// </summary>
    public static readonly LibraryContentType[] ImporterRoutedTypes =
    [
        LibraryContentType.Anime, LibraryContentType.Movie, LibraryContentType.Tv, LibraryContentType.Music,
        LibraryContentType.Manga, LibraryContentType.LightNovel, LibraryContentType.Book, LibraryContentType.Audiobook
    ];

    /// <summary>The reading and audiobook importers read their destination as an import-settings view built from Storage (<see cref="WithRoutedLibrariesAsync"/>).</summary>
    private static readonly LibraryContentType[] SettingsViewTypes = [LibraryContentType.Manga, LibraryContentType.LightNovel, LibraryContentType.Book, LibraryContentType.Audiobook];

    /// <summary>Every content type the Admin Storage page lets the owner route.</summary>
    public static IReadOnlyList<LibraryContentType> ManagedTypes => ImporterRoutedTypes;

    /// <summary>The acquisition kind whose importer serves a content type, or null for a type without one.</summary>
    public static MediaAcquisitionKind? KindOf(LibraryContentType contentType) =>
        contentType switch
        {
            LibraryContentType.Anime => MediaAcquisitionKind.Anime,
            LibraryContentType.Manga => MediaAcquisitionKind.Manga,
            LibraryContentType.LightNovel => MediaAcquisitionKind.LightNovel,
            LibraryContentType.Book => MediaAcquisitionKind.Book,
            LibraryContentType.Movie => MediaAcquisitionKind.Movie,
            LibraryContentType.Tv => MediaAcquisitionKind.Tv,
            LibraryContentType.Audiobook => MediaAcquisitionKind.Audiobook,
            LibraryContentType.Music => MediaAcquisitionKind.Music,
            _ => null
        };

    /// <summary>The content type whose default root receives the imports of a media type, or null for a media type that has none.</summary>
    public static LibraryContentType? ContentTypeOf(MediaAcquisitionKind kind) =>
        kind switch
        {
            MediaAcquisitionKind.Anime => LibraryContentType.Anime,
            MediaAcquisitionKind.Manga => LibraryContentType.Manga,
            MediaAcquisitionKind.LightNovel => LibraryContentType.LightNovel,
            MediaAcquisitionKind.Book => LibraryContentType.Book,
            MediaAcquisitionKind.Movie => LibraryContentType.Movie,
            MediaAcquisitionKind.Tv => LibraryContentType.Tv,
            MediaAcquisitionKind.Audiobook => LibraryContentType.Audiobook,
            MediaAcquisitionKind.Music => LibraryContentType.Music,
            _ => null
        };

    /// <summary>The video types: a root serving Anime cannot also serve them, because the Anime scanner would read their folders as anime.</summary>
    private static readonly LibraryContentType[] VideoRoutedTypes = [LibraryContentType.Movie, LibraryContentType.Tv];

    /// <summary>Why an importer cannot place media: no enabled default LibraryRoot is configured for the content type.</summary>
    public static string MissingDefaultMessage(LibraryContentType contentType) =>
        $"No default {(contentType == LibraryContentType.Tv ? "TV" : contentType.ToString())} library root is configured. Choose one under Admin → Storage; the import resumes automatically.";

    public async Task<LibraryRootRoute?> ResolveDefaultAsync(LibraryContentType contentType, CancellationToken cancellationToken = default) =>
        await (
            from assignment in db.LibraryRootContentAssignments.AsNoTracking()
            join root in db.LibraryRoots.AsNoTracking() on assignment.LibraryRootId equals root.Id
            where assignment.ContentType == contentType && assignment.IsDefault && root.IsEnabled
            select new LibraryRootRoute(root.Id, root.Name, root.Path, root.PlacementPolicy, true, true))
        .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Gives the reading and audiobook importers their destination: for every such type with an enabled default root, that root and its placement
    /// policy become the library folder and import mode of its entry in the import settings (the inbox and remote path mappings are kept). The
    /// settings store no library folder of its own, so a type without a default root has no destination and its importer waits.
    /// </summary>
    public async Task<AnimeImportSettingsState> WithRoutedLibrariesAsync(AnimeImportSettingsState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var types = SettingsViewTypes;
        var defaults = await (
                from assignment in db.LibraryRootContentAssignments.AsNoTracking()
                join root in db.LibraryRoots.AsNoTracking() on assignment.LibraryRootId equals root.Id
                where assignment.IsDefault && root.IsEnabled && types.Contains(assignment.ContentType)
                select new { assignment.ContentType, root.Path, root.PlacementPolicy })
            .ToListAsync(cancellationToken);
        var libraries = new Dictionary<MediaAcquisitionKind, MediaLibraryTarget>(state.MediaLibraries ?? []);
        foreach (var type in types)
        {
            if (KindOf(type) is { } kind && libraries.TryGetValue(kind, out var stored))
            {
                libraries[kind] = stored with { LibraryRoot = null, ImportMode = null };
            }
        }

        foreach (var route in defaults)
        {
            if (KindOf(route.ContentType) is not { } kind)
            {
                continue;
            }

            libraries[kind] = libraries.GetValueOrDefault(kind, new MediaLibraryTarget()) with { LibraryRoot = route.Path, ImportMode = ImportFileTransfer.ModeFor(route.PlacementPolicy) };
        }

        return state with { MediaLibraries = libraries };
    }

    public async Task<IReadOnlyList<LibraryRootRoute>> ListAsync(LibraryContentType contentType, CancellationToken cancellationToken = default) =>
        await (
            from assignment in db.LibraryRootContentAssignments.AsNoTracking()
            join root in db.LibraryRoots.AsNoTracking() on assignment.LibraryRootId equals root.Id
            where assignment.ContentType == contentType
            orderby assignment.IsDefault descending, root.Name, root.Id
            select new LibraryRootRoute(root.Id, root.Name, root.Path, root.PlacementPolicy, assignment.IsDefault, root.IsEnabled))
        .ToListAsync(cancellationToken);

    public async Task SetSupportedAsync(Guid libraryRootId, LibraryContentType contentType, bool supported, CancellationToken cancellationToken = default)
    {
        var libraryRoot = await db.LibraryRoots.AsNoTracking().SingleOrDefaultAsync(root => root.Id == libraryRootId, cancellationToken)
            ?? throw new InvalidOperationException("The LibraryRoot no longer exists.");

        var assignment = await db.LibraryRootContentAssignments.SingleOrDefaultAsync(
            row => row.LibraryRootId == libraryRootId && row.ContentType == contentType,
            cancellationToken);

        if (supported)
        {
            if (assignment is null)
            {
                await EnsureCompatibleAsync(libraryRoot, contentType, cancellationToken);
                db.LibraryRootContentAssignments.Add(new LibraryRootContentAssignment { LibraryRootId = libraryRootId, ContentType = contentType });
                await db.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        if (assignment is not null)
        {
            db.LibraryRootContentAssignments.Remove(assignment);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task SetDefaultAsync(LibraryContentType contentType, Guid? libraryRootId, CancellationToken cancellationToken = default)
    {
        if (libraryRootId is { } selectedRootId)
        {
            var root = await db.LibraryRoots.AsNoTracking().Where(candidate => candidate.Id == selectedRootId)
                .Select(candidate => new { candidate.IsEnabled })
                .SingleOrDefaultAsync(cancellationToken);

            if (root is null)
            {
                throw new InvalidOperationException("The LibraryRoot no longer exists.");
            }

            if (!root.IsEnabled)
            {
                throw new InvalidOperationException("A disabled LibraryRoot cannot be the default destination.");
            }

            var supportsContent = await db.LibraryRootContentAssignments.AsNoTracking()
                .AnyAsync(row => row.LibraryRootId == selectedRootId && row.ContentType == contentType, cancellationToken);

            if (!supportsContent)
            {
                throw new InvalidOperationException("The LibraryRoot does not support this content type.");
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.LibraryRootContentAssignments.Where(row => row.ContentType == contentType && row.IsDefault)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsDefault, false), cancellationToken);

        if (libraryRootId is { } rootId)
        {
            var updated = await db.LibraryRootContentAssignments.Where(row => row.LibraryRootId == rootId && row.ContentType == contentType)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsDefault, true), cancellationToken);

            if (updated != 1)
            {
                throw new InvalidOperationException("The LibraryRoot no longer supports this content type.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Makes an enabled LibraryRoot the default destination of a content type and sets how imports are placed into it. The root becomes
    /// a supported root of the type when it was not yet. The placement policy belongs to the root, so it applies to every content type
    /// the root serves.
    /// </summary>
    public async Task AssignDefaultAsync(LibraryContentType contentType, Guid libraryRootId, LibraryPlacementPolicy placementPolicy, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(placementPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(placementPolicy));
        }

        var root = await db.LibraryRoots.SingleOrDefaultAsync(candidate => candidate.Id == libraryRootId, cancellationToken)
            ?? throw new InvalidOperationException("The LibraryRoot no longer exists.");

        if (!root.IsEnabled)
        {
            throw new InvalidOperationException("A disabled LibraryRoot cannot be the default destination.");
        }

        if (VideoRoutedTypes.Contains(contentType))
        {
            await EnsureNoAnimeConflictAsync(root, cancellationToken);
        }

        await SetSupportedAsync(libraryRootId, contentType, true, cancellationToken);
        await SetDefaultAsync(contentType, libraryRootId, cancellationToken);

        if (root.PlacementPolicy != placementPolicy)
        {
            root.PlacementPolicy = placementPolicy;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>The first LibraryRoot whose folder is, contains or lies inside <paramref name="path"/>.</summary>
    public async Task<LibraryRoot?> FindOverlappingRootAsync(string path, CancellationToken cancellationToken = default) =>
        (await db.LibraryRoots.AsNoTracking().ToListAsync(cancellationToken)).FirstOrDefault(root => StoragePaths.Overlaps(path, root.Path));

    /// <summary>Whether the Anime scanner reads this root: it has an explicit Anime assignment. The assignment is the only source.</summary>
    public async Task<bool> ServesAnimeAsync(Guid libraryRootId, CancellationToken cancellationToken = default) =>
        await db.LibraryRootContentAssignments.AsNoTracking().AnyAsync(row => row.LibraryRootId == libraryRootId && row.ContentType == LibraryContentType.Anime, cancellationToken);

    // A root serves Anime or Movie/TV, never both: the Anime scanner would read its movie folders as anime, and Movie/TV placement
    // would write into an Anime tree. A Movie or TV root also must not sit inside or around an Anime root.
    private async Task EnsureCompatibleAsync(LibraryRoot root, LibraryContentType contentType, CancellationToken cancellationToken)
    {
        if (contentType == LibraryContentType.Anime)
        {
            if (await db.LibraryRootContentAssignments.AsNoTracking().AnyAsync(row => row.LibraryRootId == root.Id && VideoRoutedTypes.Contains(row.ContentType), cancellationToken))
            {
                throw new LibraryRootConflictException($"LibraryRoot '{root.Name}' is a Movie or TV destination and cannot also serve Anime.");
            }

            return;
        }

        if (VideoRoutedTypes.Contains(contentType))
        {
            await EnsureNoAnimeConflictAsync(root, cancellationToken);
        }
    }

    private async Task EnsureNoAnimeConflictAsync(LibraryRoot root, CancellationToken cancellationToken)
    {
        if (await ServesAnimeAsync(root.Id, cancellationToken))
        {
            throw new LibraryRootConflictException($"LibraryRoot '{root.Name}' serves Anime and cannot also be a Movie or TV destination.");
        }

        var animeRoots = await db.LibraryRoots.AsNoTracking().Where(other => other.IsEnabled && other.Id != root.Id).ServingAnime(db).ToListAsync(cancellationToken);
        if (animeRoots.FirstOrDefault(other => StoragePaths.Overlaps(root.Path, other.Path)) is { } overlapping)
        {
            throw new LibraryRootConflictException($"LibraryRoot '{root.Name}' overlaps the Anime root '{overlapping.Name}'.");
        }
    }
}

/// <summary>A LibraryRoot cannot take a content type because it conflicts with a root that serves another one.</summary>
public sealed class LibraryRootConflictException(string message) : InvalidOperationException(message);

public sealed record LibraryRootRoute(Guid LibraryRootId, string Name, string Path, LibraryPlacementPolicy PlacementPolicy, bool IsDefault, bool IsEnabled);
