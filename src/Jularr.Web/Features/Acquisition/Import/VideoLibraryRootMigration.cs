using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Storage;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Import;

/// <summary>
/// One-time move of the Movie and TV library folder (and its import mode) from the per-media import settings into the canonical
/// LibraryRoot routing of Storage (#815). First every enabled root without a content assignment becomes an explicit Anime root, because
/// until now such a root was an Anime root by inference only. Then the folder becomes a LibraryRoot that supports the content type and is
/// its default, and the import mode becomes that root's placement policy. An existing root of the same path is reused; a disabled root
/// without content assignments (created implicitly by an older in-place import) is adopted. A folder that is an Anime root, overlaps one,
/// is malformed or sits on a disabled root is not routed: the type is left without a default, the log tells the owner to choose one under
/// Admin → Storage, and the import waits until then. A default the owner already chose is never replaced. The entries are cleared and the
/// version is bumped in every case, so a bad legacy value can never fail startup. No media is moved.
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
    public static async Task<int> MigrateAsync(AnimeImportSettingsStore store, AppDbContext db, LibraryRootRoutingService routing, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(routing);

        var state = await store.LoadAsync(cancellationToken);
        if (state.Version >= CanonicalVideoRootsVersion)
        {
            return 0;
        }

        var unassigned = await db.LibraryRoots.AsNoTracking()
            .Where(root => root.IsEnabled && !db.LibraryRootContentAssignments.Any(assignment => assignment.LibraryRootId == root.Id))
            .Select(root => root.Id)
            .ToListAsync(cancellationToken);
        foreach (var rootId in unassigned)
        {
            await routing.SetSupportedAsync(rootId, LibraryContentType.Anime, true, cancellationToken);
        }

        var migrated = 0;
        var firstRoutedPath = new Dictionary<string, (MediaAcquisitionKind Kind, ImportMode Mode)>(StringComparer.Ordinal);
        foreach (var (kind, contentType, rootName) in VideoTypes)
        {
            if (state.LibraryFor(kind) is not { } target)
            {
                continue;
            }

            var mode = target.ImportMode ?? state.DefaultImportMode;
            if (await routing.ResolveDefaultAsync(contentType, cancellationToken) is not null)
            {
                log?.Invoke($"The {kind} library folder '{target.LibraryRoot}' was not moved: a default {kind} root is already chosen in Storage.");
                continue;
            }

            ResolvedRoot? resolved = null;
            try
            {
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.LibraryRoot!.Trim()));
                resolved = await ResolveRootAsync(db, path, rootName, PolicyFor(mode), cancellationToken);
                await routing.AssignDefaultAsync(contentType, resolved.Root.Id, resolved.Root.PlacementPolicy, cancellationToken);

                if (firstRoutedPath.TryGetValue(path, out var shared) && shared.Mode != mode)
                {
                    log?.Invoke($"The {kind} and {shared.Kind} folder are the same root; it keeps the {shared.Kind} import mode ({shared.Mode}) as its placement policy and the {kind} mode ({mode}) is dropped. Change it under Admin → Storage.");
                }

                firstRoutedPath.TryAdd(path, (kind, mode));
                migrated++;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException or IOException)
            {
                await UndoAsync(db, resolved, cancellationToken);
                log?.Invoke($"The {kind} library folder '{target.LibraryRoot}' could not be moved into Storage ({exception.Message}). {kind} imports wait until a default root is chosen under Admin → Storage.");
            }
        }

        // Cleared only after Storage holds the destination (or the log named why it does not), so an interrupted run repeats safely.
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
            log,
            cancellationToken);
        if (moved > 0)
        {
            log($"Moved the Movie/TV library folder into Storage default destinations ({moved} media type(s)).");
        }
    }

    // A root of the same path is reused; a disabled one that no content type uses was created implicitly by an older in-place import
    // and is adopted. Any other disabled root cannot be a destination, which AssignDefaultAsync reports.
    private static async Task<ResolvedRoot> ResolveRootAsync(AppDbContext db, string path, string rootName, LibraryPlacementPolicy policy, CancellationToken cancellationToken)
    {
        var roots = await db.LibraryRoots.ToListAsync(cancellationToken);
        var root = roots.FirstOrDefault(candidate => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.Path)), path, StringComparison.Ordinal));
        ResolvedRoot resolved;
        if (root is null)
        {
            root = new LibraryRoot { Name = rootName, Path = path, PlacementPolicy = policy };
            db.LibraryRoots.Add(root);
            resolved = new ResolvedRoot(root, Created: true, Adopted: false, policy);
        }
        else if (!root.IsEnabled && !await db.LibraryRootContentAssignments.AnyAsync(assignment => assignment.LibraryRootId == root.Id, cancellationToken))
        {
            resolved = new ResolvedRoot(root, Created: false, Adopted: true, root.PlacementPolicy);
            root.IsEnabled = true;
            root.PlacementPolicy = policy;
        }
        else
        {
            resolved = new ResolvedRoot(root, Created: false, Adopted: false, root.PlacementPolicy);
        }

        await db.SaveChangesAsync(cancellationToken);
        return resolved;
    }

    // A root that was created or adopted only for a destination that Storage then refused must not stay behind as an enabled, unassigned
    // root: the Anime scanner would read it.
    private static async Task UndoAsync(AppDbContext db, ResolvedRoot? resolved, CancellationToken cancellationToken)
    {
        if (resolved is null)
        {
            return;
        }

        if (resolved.Created)
        {
            db.LibraryRoots.Remove(resolved.Root);
        }
        else if (resolved.Adopted)
        {
            resolved.Root.IsEnabled = false;
            resolved.Root.PlacementPolicy = resolved.PreviousPolicy;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record ResolvedRoot(LibraryRoot Root, bool Created, bool Adopted, LibraryPlacementPolicy PreviousPolicy);

    private static LibraryPlacementPolicy PolicyFor(ImportMode mode) =>
        mode switch
        {
            ImportMode.Move => LibraryPlacementPolicy.Move,
            ImportMode.Copy => LibraryPlacementPolicy.Copy,
            ImportMode.Hardlink => LibraryPlacementPolicy.Hardlink,
            _ => LibraryPlacementPolicy.HardlinkOrCopy
        };
}
