using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Storage;

/// <summary>Owns specialized LibraryRoot content routing and the effective placement policy returned to importers.</summary>
public sealed class LibraryRootRoutingService(AppDbContext db)
{
    /// <summary>
    /// The content types whose importers already resolve their destination through this service. Types not listed here
    /// still read their legacy per-media library folder until their importer is migrated.
    /// </summary>
    public static readonly LibraryContentType[] ImporterRoutedTypes = [LibraryContentType.Movie, LibraryContentType.Tv, LibraryContentType.Music];

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
