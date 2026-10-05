using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Storage;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Import;

/// <summary>
/// One-time move of the Movie and TV library folder (and its import mode) from the per-media import settings into the canonical
/// LibraryRoot routing of Storage (#815). The folder becomes a LibraryRoot that supports the content type and is its default, the
/// import mode becomes that root's placement policy, and the per-media entries are cleared so nothing reads them again. An existing
/// root of the same path is reused; a disabled root without content assignments (created implicitly by an older in-place import) is
/// adopted. A default the owner already chose in Storage is never replaced. No media is moved.
/// </summary>
public static class VideoLibraryRootMigration
{
    /// <summary>Import-settings version from which the Movie and TV destination is canonical in Storage.</summary>
    public const int CanonicalVideoRootsVersion = 3;

    private static readonly (MediaAcquisitionKind Kind, LibraryContentType ContentType, string RootName)[] VideoTypes =
    [
        (MediaAcquisitionKind.Movie, LibraryContentType.Movie, "Movies"),
        (MediaAcquisitionKind.Tv, LibraryContentType.Tv, "TV")
    ];

    /// <returns>The number of media types whose folder was moved into Storage.</returns>
    public static async Task<int> MigrateAsync(AnimeImportSettingsStore store, AppDbContext db, LibraryRootRoutingService routing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(routing);

        var state = await store.LoadAsync(cancellationToken);
        if (state.Version >= CanonicalVideoRootsVersion)
        {
            return 0;
        }

        var migrated = 0;
        foreach (var (kind, contentType, rootName) in VideoTypes)
        {
            if (state.LibraryFor(kind) is not { } target)
            {
                continue;
            }

            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.LibraryRoot!.Trim()));
            var policy = PolicyFor(target.ImportMode ?? state.DefaultImportMode);
            var roots = await db.LibraryRoots.ToListAsync(cancellationToken);
            var root = roots.FirstOrDefault(candidate => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.Path)), path, StringComparison.Ordinal));
            if (root is null)
            {
                root = new LibraryRoot { Name = rootName, Path = path, PlacementPolicy = policy };
                db.LibraryRoots.Add(root);
            }
            else if (!root.IsEnabled && !await db.LibraryRootContentAssignments.AnyAsync(assignment => assignment.LibraryRootId == root.Id, cancellationToken))
            {
                root.IsEnabled = true;
                root.PlacementPolicy = policy;
            }

            await db.SaveChangesAsync(cancellationToken);
            await routing.SetSupportedAsync(root.Id, contentType, true, cancellationToken);
            if (await routing.ResolveDefaultAsync(contentType, cancellationToken) is null)
            {
                await routing.SetDefaultAsync(contentType, root.Id, cancellationToken);
            }

            migrated++;
        }

        // Cleared only after Storage holds the destination, so an interrupted run repeats safely.
        await store.UpdateAsync(
            current =>
            {
                var libraries = new Dictionary<MediaAcquisitionKind, MediaLibraryTarget>(current.MediaLibraries ?? []);
                foreach (var (kind, _, _) in VideoTypes)
                {
                    if (!libraries.TryGetValue(kind, out var existing))
                    {
                        continue;
                    }

                    var cleared = existing with { LibraryRoot = null, ImportMode = null };
                    if (cleared.IsEmpty)
                    {
                        libraries.Remove(kind);
                    }
                    else
                    {
                        libraries[kind] = cleared;
                    }
                }

                return current with { Version = CanonicalVideoRootsVersion, MediaLibraries = libraries };
            },
            cancellationToken);
        return migrated;
    }

    public static async Task RunAtStartupAsync(IServiceProvider services, Action<string> log, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var moved = await MigrateAsync(
            scope.ServiceProvider.GetRequiredService<AnimeImportSettingsStore>(),
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<LibraryRootRoutingService>(),
            cancellationToken);
        if (moved > 0)
        {
            log($"Moved the Movie/TV library folder into Storage default destinations ({moved} media type(s)).");
        }
    }

    private static LibraryPlacementPolicy PolicyFor(ImportMode mode) =>
        mode switch
        {
            ImportMode.Move => LibraryPlacementPolicy.Move,
            ImportMode.Copy => LibraryPlacementPolicy.Copy,
            ImportMode.Hardlink => LibraryPlacementPolicy.Hardlink,
            _ => LibraryPlacementPolicy.HardlinkOrCopy
        };
}
