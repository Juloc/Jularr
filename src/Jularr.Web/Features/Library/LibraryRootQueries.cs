using Jularr.Web.Data;

namespace Jularr.Web.Features.Library;

public static class LibraryRootQueries
{
    /// <summary>
    /// The roots the Anime scanner may read: those with an explicit Anime content assignment, which is the only source. A Movie or TV
    /// root is not an Anime library: scanning it would turn its movie folders into Anime.
    /// </summary>
    public static IQueryable<LibraryRoot> ServingAnime(this IQueryable<LibraryRoot> roots, AppDbContext db) =>
        roots.Where(root => db.LibraryRootContentAssignments.Any(assignment => assignment.LibraryRootId == root.Id && assignment.ContentType == LibraryContentType.Anime));
}
