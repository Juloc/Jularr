using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Collections;

/// <summary>
/// The durable relational store for collections and their membership (#427). Collections are proper
/// relational data (rules, ordered membership, per-item match reasons), so they live in EF tables rather
/// than a JSON settings store. All writes are scoped to the owning profile so one profile can never read or
/// mutate another's collections.
/// </summary>
public sealed class CollectionStore(AppDbContext db)
{
    public async Task<IReadOnlyList<Collection>> ListAsync(string profileId, CancellationToken cancellationToken) =>
        await db.Collections.AsNoTracking()
            .Where(x => x.ProfileId == profileId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

    public Task<Collection?> GetAsync(string profileId, Guid id, CancellationToken cancellationToken) =>
        db.Collections.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.ProfileId == profileId, cancellationToken);

    public async Task<Collection> CreateAsync(Collection collection, CancellationToken cancellationToken)
    {
        collection.CreatedAt = DateTime.UtcNow;
        collection.UpdatedAt = collection.CreatedAt;
        if (collection.SortOrder == 0)
        {
            collection.SortOrder = 1 + await db.Collections
                .Where(x => x.ProfileId == collection.ProfileId)
                .Select(x => (int?)x.SortOrder)
                .MaxAsync(cancellationToken) ?? 0;
        }

        db.Collections.Add(collection);
        await db.SaveChangesAsync(cancellationToken);
        return collection;
    }

    public async Task<bool> UpdateAsync(
        string profileId,
        Guid id,
        string name,
        string? description,
        string? ruleJson,
        CancellationToken cancellationToken)
    {
        var collection = await db.Collections
            .FirstOrDefaultAsync(x => x.Id == id && x.ProfileId == profileId, cancellationToken);
        if (collection is null)
        {
            return false;
        }

        collection.Name = name;
        collection.Description = description;
        if (collection.Kind == CollectionKind.Smart)
        {
            collection.RuleJson = ruleJson;
            // A changed rule invalidates the previous materialization.
            collection.LastMaterializedAt = null;
        }

        collection.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(string profileId, Guid id, CancellationToken cancellationToken)
    {
        var collection = await db.Collections
            .FirstOrDefaultAsync(x => x.Id == id && x.ProfileId == profileId, cancellationToken);
        if (collection is null)
        {
            return false;
        }

        var items = await db.CollectionItems.Where(x => x.CollectionId == id).ToListAsync(cancellationToken);
        db.CollectionItems.RemoveRange(items);
        db.Collections.Remove(collection);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<CollectionItem>> GetItemsAsync(Guid collectionId, CancellationToken cancellationToken) =>
        await db.CollectionItems.AsNoTracking()
            .Where(x => x.CollectionId == collectionId)
            .OrderBy(x => x.Position)
            .ThenBy(x => x.AddedAt)
            .ToListAsync(cancellationToken);

    /// <summary>Adds a work to a manual collection at the end, ignoring a duplicate.</summary>
    public async Task<bool> AddManualItemAsync(Guid collectionId, long workId, CancellationToken cancellationToken)
    {
        var exists = await db.CollectionItems
            .AnyAsync(x => x.CollectionId == collectionId && x.WorkId == workId, cancellationToken);
        if (exists)
        {
            return false;
        }

        var nextPosition = 1 + await db.CollectionItems
            .Where(x => x.CollectionId == collectionId)
            .Select(x => (int?)x.Position)
            .MaxAsync(cancellationToken) ?? 0;

        db.CollectionItems.Add(new CollectionItem
        {
            CollectionId = collectionId,
            WorkId = workId,
            Source = CollectionItemSource.Manual,
            Position = nextPosition,
            AddedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RemoveItemAsync(Guid collectionId, long workId, CancellationToken cancellationToken)
    {
        var item = await db.CollectionItems
            .FirstOrDefaultAsync(x => x.CollectionId == collectionId && x.WorkId == workId, cancellationToken);
        if (item is null)
        {
            return false;
        }

        db.CollectionItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Applies an explicit ordering to a manual collection; unknown works are ignored.</summary>
    public async Task ReorderAsync(Guid collectionId, IReadOnlyList<long> orderedWorkIds, CancellationToken cancellationToken)
    {
        var items = await db.CollectionItems
            .Where(x => x.CollectionId == collectionId)
            .ToListAsync(cancellationToken);
        var order = orderedWorkIds
            .Select((workId, index) => (workId, index))
            .ToDictionary(x => x.workId, x => x.index);

        foreach (var item in items)
        {
            if (order.TryGetValue(item.WorkId, out var position))
            {
                item.Position = position;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces a smart collection's rule-derived membership with a freshly materialized set (#427):
    /// rule items are cleared and re-inserted with their rank and match reasons, and the collection's
    /// <see cref="Collection.LastMaterializedAt"/> is stamped. Manual items (if any) are left untouched.
    /// </summary>
    public async Task ReplaceSmartItemsAsync(
        Guid collectionId,
        IReadOnlyList<(long WorkId, string Reason)> members,
        DateTime materializedAt,
        CancellationToken cancellationToken)
    {
        var existing = await db.CollectionItems
            .Where(x => x.CollectionId == collectionId && x.Source == CollectionItemSource.Rule)
            .ToListAsync(cancellationToken);
        db.CollectionItems.RemoveRange(existing);

        var position = 0;
        foreach (var (workId, reason) in members)
        {
            db.CollectionItems.Add(new CollectionItem
            {
                CollectionId = collectionId,
                WorkId = workId,
                Source = CollectionItemSource.Rule,
                Position = position++,
                MatchReason = reason,
                AddedAt = materializedAt
            });
        }

        var collection = await db.Collections.FirstOrDefaultAsync(x => x.Id == collectionId, cancellationToken);
        if (collection is not null)
        {
            collection.LastMaterializedAt = materializedAt;
            collection.UpdatedAt = materializedAt;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
