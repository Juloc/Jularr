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
    public static readonly LibraryContentType[] ImporterRoutedTypes = [LibraryContentType.Movie, LibraryContentType.Tv];

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
        if (!await db.LibraryRoots.AsNoTracking().AnyAsync(root => root.Id == libraryRootId, cancellationToken))
        {
            throw new InvalidOperationException("The LibraryRoot no longer exists.");
        }

        var assignment = await db.LibraryRootContentAssignments.SingleOrDefaultAsync(
            row => row.LibraryRootId == libraryRootId && row.ContentType == contentType,
            cancellationToken);

        if (supported)
        {
            if (assignment is null)
            {
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

        await SetSupportedAsync(libraryRootId, contentType, true, cancellationToken);
        await SetDefaultAsync(contentType, libraryRootId, cancellationToken);

        if (root.PlacementPolicy != placementPolicy)
        {
            root.PlacementPolicy = placementPolicy;
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}

public sealed record LibraryRootRoute(Guid LibraryRootId, string Name, string Path, LibraryPlacementPolicy PlacementPolicy, bool IsDefault, bool IsEnabled);
