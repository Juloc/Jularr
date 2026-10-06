using Jularr.Web.Features.Acquisition.Access;
using System.Net;
using System.Text;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

internal static class SabnzbdTestSupport
{
    public static SabnzbdConnection Connection() =>
        new(
            new SabnzbdSettings(
                "http://sabnzbd:8080",
                BooksCategory: "books",
                AnimeCategory: "anime"),
            "secret-key");

    public static DirectoryInfo CreateTemporaryDirectory() =>
        Directory.CreateDirectory(
            Path.Combine(
                Path.GetTempPath(),
                $"jularr-sab-{Guid.NewGuid():N}"));

    public static IConfiguration Configuration(
        IReadOnlyDictionary<string, string?>? values = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>())
            .Build();

    public static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };

    public static async Task<AppDbContext> CreateDatabaseAsync(string path)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options;

        var db = new AppDbContext(options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }

    /// <summary>
    /// A configured SABnzbd environment backed by a fake client and an
    /// isolated data directory/database.
    /// </summary>
    public static async Task<SabnzbdTestEnvironment> CreateEnvironmentAsync()
    {
        var directory = CreateTemporaryDirectory();
        var protection = new EphemeralDataProtectionProvider();

        var clients = new DownloadClientStore(protection, directory);
        await clients.SaveAsync(
            new DownloadClientEntry(
                Guid.NewGuid(),
                "SABnzbd",
                DownloadClientType.Sabnzbd,
                Enabled: true,
                Priority: 1,
                new DownloadClientSettings("http://sabnzbd:8080", new Dictionary<MediaAcquisitionKind, string?> { [MediaAcquisitionKind.Book] = "books", [MediaAcquisitionKind.Anime] = "anime" }),
                "secret-key"));

        var db = await CreateDatabaseAsync(Path.Combine(directory.FullName, "jularr.db"));
        return new SabnzbdTestEnvironment(directory, protection, db);
    }
}

internal sealed class SabnzbdTestEnvironment(
    DirectoryInfo directory,
    IDataProtectionProvider protection,
    AppDbContext db) : IAsyncDisposable
{
    public DirectoryInfo Directory { get; } = directory;
    public IDataProtectionProvider Protection { get; } = protection;
    public AppDbContext Db { get; private set; } = db;
    public FakeSabnzbdClient Client { get; } = new();

    /// <summary>A fresh store instance reads the persisted file, as after a restart.</summary>
    public SabnzbdAcquisitionStore NewAcquisitionStore() =>
        new(Protection, Directory);

    /// <summary>A fresh store instance reads the persisted file, as after a restart.</summary>
    public DownloadClientStore NewDownloadClientStore() =>
        new(Protection, Directory);

    public DownloadClientSubmissionService NewSubmissionService() =>
        new(
            new SabnzbdDownloadClient(Client),
            new DownloadClientSelector(NewDownloadClientStore(), new AcquisitionHealthStore(Directory)),
            Db,
            NullLogger<DownloadClientSubmissionService>.Instance);

    public SabnzbdDownloadService NewDownloadService(SabnzbdAcquisitionStore store) =>
        new(NewSubmissionService(), NewDownloadClientStore(), Client, store, Db);

    public SabnzbdAcquisitionService NewAcquisitionService(SabnzbdAcquisitionStore store) =>
        new(NewDownloadService(store), store, Db);

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        Directory.Delete(recursive: true);
    }
}

internal sealed class FakeSabnzbdClient : ISabnzbdClient
{
    private int nextId;

    public SabnzbdQueueSnapshot Queue { get; set; } = new(false, null, null, []);
    public SabnzbdHistorySnapshot History { get; set; } = new([]);
    public List<SabnzbdGrabRequest> Grabs { get; } = [];

    /// <summary>Thrown by <see cref="GrabAsync"/> after the attempt is recorded, as an interrupted submission.</summary>
    public Exception? GrabException { get; set; }
    public List<string> Cancelled { get; } = [];
    public List<SabnzbdConnection> CancelConnections { get; } = [];
    public List<string> Retried { get; } = [];
    public List<SabnzbdConnection> RetryConnections { get; } = [];
    public Queue<SabnzbdGrabResult> GrabResults { get; } = new();

    public Task<SabnzbdConnectionTestResult> TestAsync(
        SabnzbdConnection connection,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SabnzbdConnectionTestResult(true, "test", CanMonitor: true));

    /// <summary>Lets a test hold a grab open before it counts, to change something while a download is being handed over.</summary>
    public Func<Task>? BeforeGrab { get; set; }

    public async Task<SabnzbdGrabResult> GrabAsync(
        SabnzbdConnection connection,
        SabnzbdGrabRequest grab,
        CancellationToken cancellationToken)
    {
        if (BeforeGrab is { } gate)
        {
            await gate();
        }

        Grabs.Add(grab);
        if (GrabException is not null)
        {
            throw GrabException;
        }

        return GrabResults.Count > 0
            ? GrabResults.Dequeue()
            : new SabnzbdGrabResult(true, [$"SABnzbd_nzo_{++nextId}"]);
    }

    public Task<SabnzbdGrabResult> AddFileAsync(
        SabnzbdConnection connection,
        Stream nzb,
        string fileName,
        string? category,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SabnzbdGrabResult(true, [$"SABnzbd_nzo_{++nextId}"]));

    public Task<SabnzbdQueueSnapshot> GetQueueAsync(
        SabnzbdConnection connection,
        CancellationToken cancellationToken) =>
        Task.FromResult(Queue);

    public Task<SabnzbdHistorySnapshot> GetHistoryAsync(
        SabnzbdConnection connection,
        IReadOnlyCollection<string>? nzoIds,
        CancellationToken cancellationToken) =>
        Task.FromResult(History);

    public Task<SabnzbdActionResult> CancelAsync(
        SabnzbdConnection connection,
        string nzoId,
        bool deleteFiles,
        CancellationToken cancellationToken)
    {
        Cancelled.Add(nzoId);
        CancelConnections.Add(connection);
        return Task.FromResult(new SabnzbdActionResult(true));
    }

    public Task<SabnzbdActionResult> DeleteHistoryAsync(
        SabnzbdConnection connection,
        string nzoId,
        bool deleteFiles,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SabnzbdActionResult(false, Error: "not in history"));

    public Task<SabnzbdActionResult> RetryAsync(
        SabnzbdConnection connection,
        string nzoId,
        CancellationToken cancellationToken)
    {
        Retried.Add(nzoId);
        RetryConnections.Add(connection);
        return Task.FromResult(new SabnzbdActionResult(true, $"{nzoId}_retry"));
    }

    public Task<SabnzbdActionResult> PauseAsync(
        SabnzbdConnection connection,
        string nzoId,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SabnzbdActionResult(true));

    public Task<SabnzbdActionResult> ResumeAsync(
        SabnzbdConnection connection,
        string nzoId,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SabnzbdActionResult(true));
}
