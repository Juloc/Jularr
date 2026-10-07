using System.Net;
using System.Net.Http.Headers;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.Novels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Jularr.Tests;

/// <summary>
/// A Light Novel and a Manga library root on disk, the shared import policy pointing at them, an
/// in-memory provider that serves cover images, and the real <see cref="ReadingCoverArtwork"/> on
/// top -- the setup #581's folder-resolution and beside-media cover tests share.
/// </summary>
/// <summary>The import settings as the importers and the artwork see them: the stored inbox and mappings plus the destinations of Storage.</summary>
internal sealed class RoutedSettings(AnimeImportSettingsStore store, AppDbContext db)
{
    public async Task<AnimeImportSettingsState> LoadAsync() => await new Jularr.Web.Features.Storage.LibraryRootRoutingService(db).WithRoutedLibrariesAsync(await store.LoadAsync());

    public Task UpdateAsync(Func<AnimeImportSettingsState, AnimeImportSettingsState> update) => store.UpdateAsync(update);
}

internal sealed class ReadingArtworkTestHost : IAsyncDisposable
{
    private ReadingArtworkTestHost(string root, AppDbContext db, AnimeImportSettingsStore settings)
    {
        Root = root;
        Db = db;
        Settings = new RoutedSettings(settings, db);
        Http = new ProviderImages();
        Store = new BesideMediaArtworkStore(db);
        Cache = new BesideMediaArtworkCache(Store, Path.Combine(root, "cache"));
        Covers = new ReadingCoverArtwork(
            db,
            settings,
            Store,
            Cache,
            new HandlerFactory(Http),
            NullLogger<ReadingCoverArtwork>.Instance,
            new Jularr.Web.Features.Storage.LibraryRootRoutingService(db));
    }

    public string Root { get; }
    public AppDbContext Db { get; }
    public RoutedSettings Settings { get; }
    public ProviderImages Http { get; }
    public BesideMediaArtworkStore Store { get; }
    public BesideMediaArtworkCache Cache { get; }
    public ReadingCoverArtwork Covers { get; }
    public IHttpClientFactory HttpClientFactory => new HandlerFactory(Http);

    public string NovelRoot => Path.Combine(Root, "light-novels");
    public string MangaRoot => Path.Combine(Root, "manga");

    public static async Task<ReadingArtworkTestHost> CreateAsync(bool withLibraryRoots = true)
    {
        var root = Path.Combine(Path.GetTempPath(), $"jularr-reading-artwork-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "jularr.db")};Pooling=False")
            .Options);
        await DatabaseMigrationBridge.UpgradeAsync(db);

        var host = new ReadingArtworkTestHost(root, db, new AnimeImportSettingsStore(root));
        Directory.CreateDirectory(host.NovelRoot);
        Directory.CreateDirectory(host.MangaRoot);
        if (withLibraryRoots)
        {
            await host.ConfigureLibraryRootsAsync(host.NovelRoot, host.MangaRoot);
        }

        return host;
    }

    /// <summary>The Light Novel and Manga roots become the default roots of Storage, which is where the importers and the artwork read them from; null clears the default.</summary>
    public async Task ConfigureLibraryRootsAsync(string? novelRoot, string? mangaRoot)
    {
        var routing = new Jularr.Web.Features.Storage.LibraryRootRoutingService(Db);
        if (novelRoot is null)
        {
            await routing.SetDefaultAsync(Jularr.Web.Features.Library.LibraryContentType.LightNovel, null);
        }
        else
        {
            await ReadingTestRoots.AssignAsync(Db, MediaAcquisitionKind.LightNovel, novelRoot);
        }

        if (mangaRoot is null)
        {
            await routing.SetDefaultAsync(Jularr.Web.Features.Library.LibraryContentType.Manga, null);
        }
        else
        {
            await ReadingTestRoots.AssignAsync(Db, MediaAcquisitionKind.Manga, mangaRoot);
        }
    }

    /// <summary>A Light Novel series whose EPUB volumes are the given (volume number, file path) pairs.</summary>
    public async Task<Guid> AddLightNovelAsync(params (int Volume, string? StoragePath)[] volumes)
    {
        var work = new NovelWork
        {
            SourceProvider = NovelEpubImportService.Provider,
            SourceKey = Guid.NewGuid().ToString("N"),
            SourceUrl = "epub://test",
            Title = "Frieren"
        };
        Db.NovelWorks.Add(work);
        foreach (var (number, storagePath) in volumes)
        {
            if (storagePath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
                await File.WriteAllBytesAsync(storagePath, [1, 2, 3]);
            }

            Db.NovelVolumes.Add(new NovelVolume
            {
                WorkId = work.Id,
                Number = number,
                Kind = NovelVolumeKinds.Epub,
                SourceKey = $"volume-{number}",
                SourceStoragePath = storagePath
            });
        }

        await Db.SaveChangesAsync();
        return work.Id;
    }

    /// <summary>A Manga series stored at <paramref name="sourcePath"/> (a folder, or a CBZ that is created).</summary>
    public async Task<Guid> AddMangaAsync(string sourcePath, bool createSource = true)
    {
        if (createSource)
        {
            if (Path.HasExtension(sourcePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
            }
            else
            {
                Directory.CreateDirectory(sourcePath);
            }
        }

        var id = Guid.NewGuid();
        await new MangaRepository(Db).UpsertSeriesAsync(id, "Frieren", sourcePath, CancellationToken.None);
        return id;
    }

    public static byte[] Image(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Serves registered image responses by URL and counts every request it receives.</summary>
    internal sealed class ProviderImages : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> routes = new(StringComparer.Ordinal);

        public int Requests { get; private set; }

        public void ServeImage(string url, byte[] bytes, string mediaType = "image/jpeg") =>
            routes[url] = () =>
            {
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            };

        public void Serve(string url, Func<HttpResponseMessage> respond) => routes[url] = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(
                routes.TryGetValue(request.RequestUri!.ToString(), out var respond)
                    ? respond()
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
