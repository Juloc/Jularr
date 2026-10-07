using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Instance;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// A database plus the JSON stores the request flow reads (capability matrix, request settings), and
/// request services for signed-in profiles of any role. Each service gets its own account context, since
/// <see cref="IHttpContextAccessor"/> keeps its context in a static <c>AsyncLocal</c> and two accounts in
/// one test need separate accessor instances.
/// </summary>
internal sealed class AcquisitionAccessFixture : IAsyncDisposable
{
    private readonly string directory;

    private AcquisitionAccessFixture(string directory, AppDbContext db)
    {
        this.directory = directory;
        Db = db;
        Capabilities = new MediaCapabilityStore(directory);
        Settings = new AcquisitionRequestSettingsStore(directory);
    }

    public AppDbContext Db { get; }
    public MediaCapabilityStore Capabilities { get; }
    public AcquisitionRequestSettingsStore Settings { get; }
    public RecordingEventPublisher Events { get; } = new();
    public AcquisitionAccessStore Store => new(Db);

    /// <summary>The read side of the request status surface over this database; instance modules are all enabled.</summary>
    public RequestStatusQuery StatusQuery(TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var works = new VideoRequestWorkResolver(Db);
        return new RequestStatusQuery(Store, new ConsumerAcquisitionQuery(Db, Store, works, clock), new RequestArtworkResolver(Db, works), new InstanceModuleStore(directory), Db, clock, NullLogger<RequestStatusQuery>.Instance);
    }

    public static async Task<AcquisitionAccessFixture> CreateAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jularr-access-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "app.db")};Foreign Keys=True")
            .Options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return new AcquisitionAccessFixture(directory, db);
    }

    /// <summary>A second context on the same database, as a second concurrent request would have.</summary>
    public AppDbContext OpenContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Db.Database.GetConnectionString()).Options);

    public AcquisitionRequestService Service(string profileId, bool isOwner, params IAcquisitionRequestExecutor[] executors) =>
        Service(profileId, isOwner ? AccountRole.Owner : AccountRole.User, executors);

    public AcquisitionRequestService Service(string profileId, AccountRole role, params IAcquisitionRequestExecutor[] executors)
    {
        var account = Account(profileId, role);
        return new AcquisitionRequestService(
            new AcquisitionAccessStore(Db),
            executors,
            account,
            new MediaCapabilityService(Capabilities),
            Settings,
            Events,
            NullLogger<AcquisitionRequestService>.Instance);
    }

    /// <summary>
    /// A request service for tests that do not care about the capability matrix or the request settings:
    /// both stores point at a folder without files, so they answer with their built-in defaults.
    /// </summary>
    public static AcquisitionRequestService DefaultsService(
        AcquisitionAccessStore store,
        CurrentAccountContext account,
        IJularrEventPublisher events)
    {
        var empty = Path.Combine(Path.GetTempPath(), $"jularr-no-settings-{Guid.NewGuid():N}");
        return new AcquisitionRequestService(
            store,
            [],
            account,
            new MediaCapabilityService(new MediaCapabilityStore(empty)),
            new AcquisitionRequestSettingsStore(empty),
            events,
            NullLogger<AcquisitionRequestService>.Instance);
    }

    public static ClaimsPrincipal Principal(string profileId, AccountRole role)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, profileId) };
        if (role != AccountRole.User)
        {
            claims.Add(new Claim(ClaimTypes.Role, role.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    public static CurrentAccountContext Account(string profileId, AccountRole role) =>
        new(new FixedHttpContextAccessor(new DefaultHttpContext { User = Principal(profileId, role) }));

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }

    private sealed class FixedHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }
}

/// <summary>Counts how often a media type's acquisition ran and what it was asked to run.</summary>
internal sealed class RecordingExecutor(MediaAcquisitionKind kind, bool fail = false, Exception? error = null) : IAcquisitionRequestExecutor
{
    public int Runs { get; private set; }
    public List<AcquisitionRequest> Requests { get; } = [];
    public MediaAcquisitionKind Kind => kind;

    public Task<AcquisitionExecution> ExecuteAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        Runs++;
        Requests.Add(request);
        return error is not null
            ? throw error
            : fail
            ? throw new InvalidOperationException("indexer down")
            : Task.FromResult(new AcquisitionExecution(AcquisitionRequestStatus.Downloading, "release", Guid.NewGuid()));
    }
}
