using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Storage;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>Gives a media type its final destination the only way the product knows: an enabled default LibraryRoot of its content type, with the placement of the mode.</summary>
public static class ReadingTestRoots
{
    public static async Task<LibraryRoot> AssignAsync(AppDbContext db, MediaAcquisitionKind kind, string path, ImportMode mode = ImportMode.Move)
    {
        var contentType = LibraryRootRoutingService.ContentTypeOf(kind) ?? throw new ArgumentOutOfRangeException(nameof(kind));
        var policy = mode switch
        {
            ImportMode.Copy => LibraryPlacementPolicy.Copy,
            ImportMode.Hardlink => LibraryPlacementPolicy.Hardlink,
            ImportMode.HardlinkOrCopy => LibraryPlacementPolicy.HardlinkOrCopy,
            _ => LibraryPlacementPolicy.Move
        };
        var root = await db.LibraryRoots.FirstOrDefaultAsync(existing => existing.Path == path);
        if (root is null)
        {
            root = new LibraryRoot { Name = $"{contentType} {Guid.NewGuid():N}"[..24], Path = path };
            db.LibraryRoots.Add(root);
            await db.SaveChangesAsync();
        }

        await new LibraryRootRoutingService(db).AssignDefaultAsync(contentType, root.Id, policy);
        return root;
    }
}
