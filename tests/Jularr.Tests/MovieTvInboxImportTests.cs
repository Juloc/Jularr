using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Movies;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Tv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// The manual inbox import of the shared Media folders flow reaches Movie and TV: the same validation and safety as every other media
/// type, and the importer places into the default root of Storage.
/// </summary>
[TestClass]
public sealed class MovieTvInboxImportTests
{
    [TestMethod]
    public void MovieAndTvAreInboxKindsBesideTheReadingTypes()
    {
        CollectionAssert.IsSubsetOf(new[] { MediaAcquisitionKind.Movie, MediaAcquisitionKind.Tv }, MediaInboxImportService.InboxKinds);
        Assert.AreEqual("Movies", MediaInboxImportService.Label(MediaAcquisitionKind.Movie));
        Assert.AreEqual("TV", MediaInboxImportService.Label(MediaAcquisitionKind.Tv));
    }

    [TestMethod]
    public async Task MovieInboxScanImportsThroughTheOperationIntoTheDefaultRoot()
    {
        await using var host = await Host.CreateAsync();
        var inbox = host.Folder("inbox-movies");
        var library = host.Folder("movies");
        File.WriteAllText(Path.Combine(inbox, "Inception.2010.1080p.BluRay.x264-GROUP.mkv"), "video");
        await host.SetInboxAsync(MediaAcquisitionKind.Movie, inbox);
        await host.SetDefaultRootAsync(LibraryContentType.Movie, library);

        var result = await host.Inboxes.RunAsync(MediaAcquisitionKind.Movie, "owner", CancellationToken.None);

        Assert.AreEqual(1, result.Imported, result.Message);
        Assert.IsTrue(File.Exists(Path.Combine(library, "Inception (2010)", "Inception (2010).mkv")));
        var operation = (await new OperationStore(host.Db).ListAsync(new OperationListFilter(Kind: MediaInboxImportService.OperationKind))).Single();
        Assert.AreEqual(OperationStatus.Succeeded, operation.Status, "The scan is one visible Operation like every other inbox scan.");
    }

    [TestMethod]
    public async Task TvInboxScanImportsEpisodesIntoTheDefaultRoot()
    {
        await using var host = await Host.CreateAsync();
        var inbox = host.Folder("inbox-tv");
        var library = host.Folder("tv");
        File.WriteAllText(Path.Combine(inbox, "Breaking.Bad.S01E02.1080p.BluRay.x264-GROUP.mkv"), "video");
        await host.SetInboxAsync(MediaAcquisitionKind.Tv, inbox);
        await host.SetDefaultRootAsync(LibraryContentType.Tv, library);

        var result = await host.Inboxes.RunAsync(MediaAcquisitionKind.Tv, "owner", CancellationToken.None);

        Assert.AreEqual(1, result.Imported, result.Message);
        Assert.AreEqual(1, Directory.GetFiles(library, "*.mkv", SearchOption.AllDirectories).Length);
        Assert.AreEqual(1, await host.Db.WorkEpisodes.CountAsync());
    }

    [TestMethod]
    public async Task AnInboxThatOverlapsTheLibraryRootIsRejectedBeforeAnythingIsImported()
    {
        await using var host = await Host.CreateAsync();
        var library = host.Folder("media");
        var movies = Directory.CreateDirectory(Path.Combine(library, "movies")).FullName;
        File.WriteAllText(Path.Combine(movies, "Existing.2001.1080p.BluRay.x264-GROUP.mkv"), "video");
        await host.SetDefaultRootAsync(LibraryContentType.Movie, movies);

        foreach (var overlapping in new[] { movies, library, Path.Combine(movies, "..", "movies"), Directory.CreateDirectory(Path.Combine(movies, "inbox")).FullName })
        {
            await host.SetInboxAsync(MediaAcquisitionKind.Movie, overlapping);

            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => host.Inboxes.RunAsync(MediaAcquisitionKind.Movie, "owner", CancellationToken.None));

            StringAssert.Contains(failure.Message, "overlaps", overlapping);
        }

        Assert.AreEqual(0, await host.Db.Movies.CountAsync());
        Assert.AreEqual(0, (await new OperationStore(host.Db).ListAsync(new OperationListFilter(Kind: MediaInboxImportService.OperationKind))).Count, "A rejected scan never starts an Operation.");
    }

    [TestMethod]
    public async Task InboxScanNeedsAConfiguredAvailableFolderAndHonoursDisabledModules()
    {
        await using var host = await Host.CreateAsync();
        await host.SetDefaultRootAsync(LibraryContentType.Movie, host.Folder("movies"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => host.Inboxes.RunAsync(MediaAcquisitionKind.Movie, "owner", CancellationToken.None));
        await host.SetInboxAsync(MediaAcquisitionKind.Movie, Path.Combine(host.Root, "offline"));
        var offline = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => host.Inboxes.RunAsync(MediaAcquisitionKind.Movie, "owner", CancellationToken.None));
        StringAssert.Contains(offline.Message, "offline");

        var inbox = host.Folder("inbox-movies");
        await host.SetInboxAsync(MediaAcquisitionKind.Movie, inbox);
        var modules = host.InboxesWithModules(InstanceModuleSettings.Default.With(InstanceModule.Movie, false));
        var disabled = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => modules.RunAsync(MediaAcquisitionKind.Movie, "owner", CancellationToken.None));
        StringAssert.Contains(disabled.Message, "disabled", "A disabled module has no inbox import.");
    }

    [TestMethod]
    public async Task AMovieInboxNestedInTheTvInboxIsLeftToTheMovieScan()
    {
        await using var host = await Host.CreateAsync();
        var tvInbox = host.Folder("inbox");
        var movieInbox = Directory.CreateDirectory(Path.Combine(tvInbox, "movies")).FullName;
        File.WriteAllText(Path.Combine(movieInbox, "Inception.2010.1080p.BluRay.x264-GROUP.mkv"), "video");
        await host.SetInboxAsync(MediaAcquisitionKind.Tv, tvInbox);
        await host.SetInboxAsync(MediaAcquisitionKind.Movie, movieInbox);
        await host.SetDefaultRootAsync(LibraryContentType.Tv, host.Folder("tv"));

        var result = await host.Inboxes.RunAsync(MediaAcquisitionKind.Tv, "owner", CancellationToken.None);

        Assert.AreEqual(0, result.Imported, "The TV scan never imports another media type's inbox.");
        Assert.AreEqual(0, await host.Db.TvSeries.CountAsync());
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider services;

        private Host(string root, AppDbContext db, ServiceProvider services)
        {
            Root = root;
            Db = db;
            this.services = services;
        }

        public string Root { get; }
        public AppDbContext Db { get; }
        public MediaInboxImportService Inboxes => services.GetRequiredService<MediaInboxImportService>();

        public static async Task<Host> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"jularr-movietv-inbox-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var db = await MediaCoreTestSupport.CreateDbAsync();
            var registry = new MediaAcquisitionRegistry([new MovieAcquisitionRegistration(), new TvAcquisitionRegistration()]);
            var bridge = new LegacyWorkBridge(db, new WorkService(db), new WorkStructureService(db));
            var routing = new LibraryRootRoutingService(db);
            var storage = new CanonicalMediaStorageService(db);
            var hardLinks = new FileSystemHardLinkCreator();
            var movieAdapter = new MovieCompletedDownloadImportAdapter(new MovieLibraryService(db, bridge), registry, routing, hardLinks, NullLogger<MovieCompletedDownloadImportAdapter>.Instance, storage);
            var tvLibrary = new TvLibraryService(db, bridge, new WorkStructureService(db));
            var tvAdapter = new TvCompletedDownloadImportAdapter(tvLibrary, registry, routing, hardLinks, NullLogger<TvCompletedDownloadImportAdapter>.Instance, storage);
            var provider = new ServiceCollection()
                .AddSingleton(db)
                .AddSingleton(routing)
                .AddSingleton(new AnimeImportSettingsStore(root))
                .AddSingleton(sp => new OperationRunner(db, sp))
                .AddSingleton<IMediaInboxImportAdapter>(movieAdapter)
                .AddSingleton<IMediaInboxImportAdapter>(tvAdapter)
                .AddSingleton<MediaInboxImportService>()
                .BuildServiceProvider();
            return new Host(root, db, provider);
        }

        public string Folder(string name) => Directory.CreateDirectory(Path.Combine(Root, name)).FullName;

        public Task SetInboxAsync(MediaAcquisitionKind kind, string inbox) =>
            services.GetRequiredService<AnimeImportSettingsStore>().UpdateAsync(state => state with
            {
                MediaLibraries = new Dictionary<MediaAcquisitionKind, MediaLibraryTarget>(state.MediaLibraries) { [kind] = new MediaLibraryTarget(InboxRoot: inbox) }
            });

        public async Task SetDefaultRootAsync(LibraryContentType contentType, string path)
        {
            var root = await Db.LibraryRoots.SingleOrDefaultAsync(candidate => candidate.Path == path);
            if (root is null)
            {
                root = new LibraryRoot { Name = contentType.ToString(), Path = path, PlacementPolicy = LibraryPlacementPolicy.Copy };
                Db.LibraryRoots.Add(root);
                await Db.SaveChangesAsync();
            }

            var routing = services.GetRequiredService<LibraryRootRoutingService>();
            await routing.SetSupportedAsync(root.Id, contentType, true);
            await routing.SetDefaultAsync(contentType, root.Id);
        }

        public MediaInboxImportService InboxesWithModules(InstanceModuleSettings settings) =>
            new(
                services.GetRequiredService<AnimeImportSettingsStore>(),
                services.GetServices<IMediaInboxImportAdapter>(),
                services.GetRequiredService<OperationRunner>(),
                services.GetRequiredService<LibraryRootRoutingService>(),
                new FixedModules(settings));

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FixedModules(InstanceModuleSettings settings) : IInstanceModuleService
    {
        public Task<InstanceModuleSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);

        public Task<bool> IsEnabledAsync(InstanceModule module, CancellationToken cancellationToken = default) => Task.FromResult(settings.IsEnabled(module));

        public Task<InstanceModuleSettings> SetAsync(InstanceModule module, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<InstanceModuleSettings> SaveAsync(InstanceModuleSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
