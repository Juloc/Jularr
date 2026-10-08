using Jularr.Web.Features.MediaCore;

namespace Jularr.Tests;

/// <summary>A per-type record belongs to exactly one canonical work: linking it again is idempotent, linking it to another work is a conflict and changes nothing.</summary>
[TestClass]
public sealed class WorkSourceLinkConflictTests
{
    [TestMethod]
    public async Task LinkingTheSameRecordToTheSameWorkTwiceIsIdempotent()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var work = await works.CreateWorkAsync(WorkMediaType.Manga, "Frieren", null, CancellationToken.None);
        var recordId = Guid.NewGuid();

        var first = await works.LinkSourceAsync(work.Id, WorkSourceKind.MangaSeries, recordId, CancellationToken.None);
        var second = await works.LinkSourceAsync(work.Id, WorkSourceKind.MangaSeries, recordId, CancellationToken.None);

        Assert.AreEqual(first.WorkId, second.WorkId);
        Assert.AreEqual(1, db.Set<WorkSourceLink>().Count(link => link.SourceId == recordId));
    }

    [TestMethod]
    public async Task LinkingARecordThatBelongsToAnotherWorkIsAConflictAndLeavesTheLinkAlone()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var owner = await works.CreateWorkAsync(WorkMediaType.Manga, "Frieren", null, CancellationToken.None);
        var other = await works.CreateWorkAsync(WorkMediaType.Manga, "Frieren (second)", null, CancellationToken.None);
        var recordId = Guid.NewGuid();
        await works.LinkSourceAsync(owner.Id, WorkSourceKind.MangaSeries, recordId, CancellationToken.None);

        var conflict = await Assert.ThrowsExactlyAsync<WorkSourceLinkConflictException>(() => works.LinkSourceAsync(other.Id, WorkSourceKind.MangaSeries, recordId, CancellationToken.None));

        Assert.AreEqual(owner.Id, conflict.LinkedWorkId);
        Assert.AreEqual(other.Id, conflict.RequestedWorkId);
        var query = new WorkQueryService(db);
        Assert.AreEqual(owner.Id, await query.ResolveWorkForSourceAsync(WorkSourceKind.MangaSeries, recordId, CancellationToken.None), "The record stays with the work it belonged to.");
    }
}
