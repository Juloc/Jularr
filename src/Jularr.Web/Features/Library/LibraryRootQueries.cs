using Jularr.Web.Data;

namespace Jularr.Web.Features.Library;

public static class LibraryRootQueries
{
    /// <summary>
    /// The roots the Anime library scanner may read. A root Storage routes only to other content types (a Movie or TV root) is not an
    /// Anime library: scanning it would turn its movie folders into Anime. Roots without any content assignment are the pre-routing
    /// Anime roots and stay scannable.
    /// </summary>
    public static IQueryable<LibraryRoot> ServingAnime(this IQueryable<LibraryRoot> roots, AppDbContext db) =>
        roots.Where(root => !db.LibraryRootContentAssignments.Any(assignment => assignment.LibraryRootId == root.Id)
            || db.LibraryRootContentAssignments.Any(assignment => assignment.LibraryRootId == root.Id && assignment.ContentType == LibraryContentType.Anime));
}
