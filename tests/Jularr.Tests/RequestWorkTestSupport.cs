using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>The pieces the Book, Light Novel and Manga request-to-Work tests share: a binder over a database and the evidence a request of each kind carries.</summary>
internal static class RequestWorkTestSupport
{
    /// <summary>The provider evidence a request of each kind is made from.</summary>
    public static (string Provider, string ExternalId, string Title) Evidence(MediaAcquisitionKind kind) => kind switch
    {
        MediaAcquisitionKind.Book => ("books-catalog", "OL82563W", "Dune"),
        MediaAcquisitionKind.LightNovel => ("anilist", "101", "Re:Zero"),
        MediaAcquisitionKind.Manga => ("anilist", "202", "Frieren"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static RequestWorkBinder Binder(AppDbContext db)
    {
        var works = new WorkService(db);
        return new RequestWorkBinder(db, works, new LegacyWorkBridge(db, works, new WorkStructureService(db)), new AcquisitionAccessStore(db), NullLogger<RequestWorkBinder>.Instance);
    }

    /// <summary>A durable request made the way the request service makes one: its Work is resolved from the evidence first (null when the evidence is not trustworthy).</summary>
    public static async Task<AcquisitionRequest> CreateRequestAsync(AppDbContext db, MediaAcquisitionKind kind, bool bound, string? provider = null, string? externalId = null, string? title = null)
    {
        var evidence = Evidence(kind);
        provider ??= evidence.Provider;
        externalId ??= evidence.ExternalId;
        title ??= evidence.Title;
        var workId = bound ? await Binder(db).ResolveAsync(kind, provider, externalId, title, CancellationToken.None) : null;
        return await new AcquisitionAccessStore(db).CreateAsync(
            new AcquisitionRequestDraft(kind, provider, externalId, title, null, null) { WorkId = workId },
            "owner",
            AcquisitionRequestStatus.Approved,
            "owner",
            CancellationToken.None);
    }

    public static async Task<Guid?> WorkOfLegacyAsync(AppDbContext db, WorkSourceKind sourceKind, Guid legacyId) =>
        await db.Set<WorkSourceLink>().AsNoTracking().Where(link => link.SourceKind == sourceKind && link.SourceId == legacyId).Select(link => (Guid?)link.WorkId).FirstOrDefaultAsync();
}
