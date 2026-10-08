using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jularr.Web.Features.MediaCore;

/// <summary>
/// The audio edition of a Book Work as a canonical <see cref="WorkEdition"/> (key and format "audiobook", the identity an audiobook import bridges as), made as soon as
/// something wants it: Monitoring decides on it apart from the Book and a request names it, before any file exists.
/// </summary>
public static class AudiobookEditions
{
    public static async Task<Guid> EnsureAsync(AppDbContext db, Guid workId, CancellationToken cancellationToken)
    {
        if (await FindAsync(db, workId, cancellationToken) is { } existing)
        {
            return existing;
        }

        var edition = new WorkEdition { WorkId = workId, EditionKey = LegacyWorkBridge.AudiobookEditionKey, Language = "und", Format = LegacyWorkBridge.AudiobookEditionFormat };
        db.WorkEditions.Add(edition);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return edition.Id;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The import (or another request) made the same edition in the same moment; that one is the edition.
            db.Entry(edition).State = EntityState.Detached;
            return await FindAsync(db, workId, cancellationToken) ?? throw new InvalidOperationException("The audiobook edition disappeared.");
        }
    }

    private static async Task<Guid?> FindAsync(AppDbContext db, Guid workId, CancellationToken cancellationToken) =>
        await db.WorkEditions.AsNoTracking()
            .Where(edition => edition.WorkId == workId && edition.EditionKey == LegacyWorkBridge.AudiobookEditionKey)
            .Select(edition => (Guid?)edition.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
