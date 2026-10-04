using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Library;

/// <summary>
/// Internal Storage routing result. The path is for trusted import/file services and must not be
/// copied into consumer-facing DTOs merely because this contract exposes it to application code.
/// </summary>
public sealed record LibraryRootRoute(
    Guid LibraryRootId,
    string Name,
    string Path,
    LibraryPlacementPolicy PlacementPolicy,
    bool IsDefault,
    bool IsEnabled);

/// <summary>
/// Canonical owner of final specialized-library routing. Importers ask this service where an
/// identified item belongs instead of maintaining per-feature final path settings.
/// </summary>
public sealed class LibraryRootRoutingService(AppDbContext db)
{
    public async Task<LibraryRootRoute?> ResolveDefaultAsync(
        LibraryContentType contentType,
        CancellationToken cancellationToken = default) =>
        await (
            from assignment in db.LibraryRootContentAssignments.AsNoTracking()
            join root in db.LibraryRoots.AsNoTracking()
                on assignment.LibraryRootId equals root.Id
            where assignment.ContentType == contentType
                  && assignment.IsDefault
                  && root.IsEnabled
            select new LibraryRootRoute(
                root.Id,
                root.Name,
                root.Path,
                root.PlacementPolicy,
                assignment.IsDefault,
                root.IsEnabled))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<LibraryRootRoute>> ListAsync(
        LibraryContentType contentType,
        CancellationToken cancellationToken = default) =>
        await (
            from assignment in db.LibraryRootContentAssignments.AsNoTracking()
            join root in db.LibraryRoots.AsNoTracking()
                on assignment.LibraryRootId equals root.Id
            where assignment.ContentType == contentType
            orderby assignment.IsDefault descending, root.Name, root.Id
            select new LibraryRootRoute(
                root.Id,
                root.Name,
                root.Path,
                root.PlacementPolicy,
                assignment.IsDefault,
                root.IsEnabled))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Adds/removes one supported content type. Removing the default leaves the type without a
    /// default rather than silently selecting another root.
    /// </summary>
    public async Task SetSupportedAsync(
        Guid libraryRootId,
        LibraryContentType contentType,
        bool supported,
        CancellationToken cancellationToken = default)
    {
        var rootExists = await db.LibraryRoots.AnyAsync(
            root => root.Id == libraryRootId,
            cancellationToken);
        if (!rootExists)
        {
            throw new InvalidOperationException("The LibraryRoot no longer exists.");
        }

        var assignment = await db.LibraryRootContentAssignments
            .SingleOrDefaultAsync(
                row => row.LibraryRootId == libraryRootId && row.ContentType == contentType,
                cancellationToken);

        if (supported)
        {
            if (assignment is null)
            {
                db.LibraryRootContentAssignments.Add(
                    new LibraryRootContentAssignment
                    {
                        LibraryRootId = libraryRootId,
                        ContentType = contentType
                    });
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

    /// <summary>
    /// Selects the one default root for a content type, or clears the default. All changes happen in
    /// one transaction and the database's filtered unique index is the final concurrency guard.
    /// </summary>
    public async Task SetDefaultAsync(
        LibraryContentType contentType,
        Guid? libraryRootId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var assignments = await db.LibraryRootContentAssignments
                .Where(row => row.ContentType == contentType)
                .ToListAsync(cancellationToken);

            LibraryRootContentAssignment? selected = null;
            if (libraryRootId is { } rootId)
            {
                var root = await db.LibraryRoots
                    .AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.Id == rootId, cancellationToken)
                    ?? throw new InvalidOperationException("The LibraryRoot no longer exists.");

                if (!root.IsEnabled)
                {
                    throw new InvalidOperationException("A disabled LibraryRoot cannot be the default destination.");
                }

                selected = assignments.SingleOrDefault(row => row.LibraryRootId == rootId)
                    ?? throw new InvalidOperationException(
                        "The LibraryRoot does not support this content type.");
            }

            // PostgreSQL enforces one default through a filtered unique index. Clear the old
            // default first, then activate the new one inside the same transaction so the database
            // never observes two default rows at once.
            foreach (var assignment in assignments.Where(row => row.IsDefault && !ReferenceEquals(row, selected)))
            {
                assignment.IsDefault = false;
            }

            await db.SaveChangesAsync(cancellationToken);

            if (selected is not null && !selected.IsDefault)
            {
                selected.IsDefault = true;
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
