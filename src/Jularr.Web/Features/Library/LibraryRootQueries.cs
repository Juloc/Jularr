using Jularr.Web.Data;
using Jularr.Web.Features.Instance;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Library;

public static class LibraryRootQueries
{
    /// <summary>
    /// The roots the Anime scanner may read: those with an explicit Anime content assignment, which is the only source. A Movie or TV
    /// root is not an Anime library: scanning it would turn its movie folders into Anime.
    /// </summary>
    public static IQueryable<LibraryRoot> ServingAnime(this IQueryable<LibraryRoot> roots, AppDbContext db) =>
        roots.Where(root => db.LibraryRootContentAssignments.Any(assignment => assignment.LibraryRootId == root.Id && assignment.ContentType == LibraryContentType.Anime));

    /// <summary>
    /// The Anime roots the background scanners (startup reconciliation, file watching, periodic checks) work on: none while the Anime
    /// module is off, so a disabled module stays cold instead of keeping its folders watched and scanned.
    /// </summary>
    public static async Task<IQueryable<LibraryRoot>> AnimeRootsForBackgroundWorkAsync(this AppDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        var modules = services.GetService<IInstanceModuleService>();
        var enabled = modules is null || (await modules.GetAsync(cancellationToken)).IsEnabled(InstanceModule.Anime);
        return db.LibraryRoots.AsNoTracking().Where(root => enabled && root.IsEnabled).ServingAnime(db);
    }
}
