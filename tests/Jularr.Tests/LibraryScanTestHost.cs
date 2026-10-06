using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Sonarr;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Features.Vocabulary;
using Jularr.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

// A real SQLite database plus the scan services wired like Program.cs, without the web host.
internal sealed class LibraryScanTestHost : IAsyncDisposable
{
    private LibraryScanTestHost(string tempRoot, ServiceProvider services, FakeMediaProbeRunner probe)
    {
        TempRoot = tempRoot;
        Services = services;
        Probe = probe;
        LibraryPath = Path.Combine(tempRoot, "anime");
    }

    public string TempRoot { get; }
    public string LibraryPath { get; }
    public ServiceProvider Services { get; }
    public FakeMediaProbeRunner Probe { get; }
    public BackgroundJobWorker? Worker { get; private set; }

    public IServiceScopeFactory ScopeFactory =>
        Services.GetRequiredService<IServiceScopeFactory>();

    public BackgroundJobQueue Queue =>
        Services.GetRequiredService<BackgroundJobQueue>();

    public LibraryScanCoordinator Scans =>
        Services.GetRequiredService<LibraryScanCoordinator>();

    public static async Task<LibraryScanTestHost> CreateAsync(bool createLibraryDirectory = true)
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"jularr-scan-lifecycle-{Guid.NewGuid():N}");
        var dictionaryPath = Path.Combine(tempRoot, "dictionary");
        var keysPath = Path.Combine(tempRoot, "keys");
        var databasePath = Path.Combine(tempRoot, "jularr.db");

        Directory.CreateDirectory(dictionaryPath);
        Directory.CreateDirectory(keysPath);
        if (createLibraryDirectory)
        {
            Directory.CreateDirectory(Path.Combine(tempRoot, "anime"));
        }

        await File.WriteAllTextAsync(Path.Combine(dictionaryPath, "jmdict-ger.tsv"), "");
        await File.WriteAllTextAsync(Path.Combine(dictionaryPath, "jmdict-eng-common.tsv"), "");

        var probe = new FakeMediaProbeRunner();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath};Foreign Keys=True"));
        services.AddSingleton<IJapaneseMorphology, EmptyMorphology>();
        services.AddSingleton<JapaneseTermExtractor>();
        services.AddSingleton(new JapaneseDictionary(dictionaryPath));
        services.AddScoped<VocabularyService>();
        services.AddScoped<SubtitleImportService>();
        services.AddSingleton<MediaProcessRunner>();
        services.AddSingleton<EmbeddedSubtitleExtractor>();
        services.AddSingleton(new SonarrConnectionStore(
            DataProtectionProvider.Create(new DirectoryInfo(keysPath))));
        services.AddSingleton<IHttpClientFactory, TestHttpClientFactory>();
        services.AddScoped<SonarrArtworkImportService>();
        services.AddScoped<SonarrArtworkSyncService>();
        services.AddSingleton<IMediaProbeRunner>(probe);
        services.AddSingleton<MediaInventoryService>();
        services.AddScoped<LibraryScanner>();
        services.AddSingleton<StorageAvailabilityCoordinator>();
        services.AddScoped<LibraryRootAvailabilityService>();
        services.AddSingleton<BackgroundJobQueue>();
        services.AddSingleton<LibraryScanCoordinator>();

        var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await DatabaseMigrationBridge.UpgradeAsync(db);
        }

        return new LibraryScanTestHost(tempRoot, provider, probe);
    }

    public async Task<LibraryRoot> AddRootAsync(string name, string? path = null, int intervalMinutes = 30)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var root = new LibraryRoot
        {
            Name = name,
            Path = path ?? LibraryPath,
            ReconciliationIntervalMinutes = intervalMinutes
        };
        db.LibraryRoots.Add(root);
        db.LibraryRootContentAssignments.Add(new LibraryRootContentAssignment { LibraryRootId = root.Id, ContentType = LibraryContentType.Anime });
        await db.SaveChangesAsync();
        return root;
    }

    public async Task StartWorkerAsync()
    {
        Worker = new BackgroundJobWorker(
            Queue,
            ScopeFactory,
            NullLogger<BackgroundJobWorker>.Instance);
        await Worker.StartAsync(CancellationToken.None);
    }

    public async Task<OperationSnapshot?> GetOperationAsync(Guid id)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await new OperationStore(db).GetAsync(id);
    }

    public async Task<IReadOnlyList<OperationSnapshot>> ListScansAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await new OperationStore(db).ListAsync(
            new OperationListFilter(Kind: LibraryScanCoordinator.OperationKind, Limit: 100));
    }

    public async Task<IReadOnlyList<OperationLogEntry>> ListLogsAsync(Guid operationId)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await new OperationStore(db).ListLogsAsync(
            new OperationLogFilter(OperationId: operationId, Limit: 500));
    }

    public async Task<OperationSnapshot> WaitForAsync(
        Guid operationId,
        Func<OperationSnapshot, bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        OperationSnapshot? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await GetOperationAsync(operationId);
            if (last is not null && predicate(last))
            {
                return last;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Operation {operationId} did not reach the expected state; last: {last?.Status} {last?.Message} {last?.Error}");
        return last!;
    }

    public string WriteMedia(string relativePath)
    {
        var fullPath = Path.Combine(LibraryPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, [0x00]);
        return fullPath;
    }

    public async ValueTask DisposeAsync()
    {
        if (Worker is not null)
        {
            await Worker.StopAsync(CancellationToken.None);
            Worker.Dispose();
        }

        await Services.DisposeAsync();

        try
        {
            if (Directory.Exists(TempRoot))
            {
                Directory.Delete(TempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class EmptyMorphology : IJapaneseMorphology
    {
        public IReadOnlyList<JapaneseMorphToken> Analyze(string text) => [];
    }
}
