using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

internal static class MediaInventoryTestSupport
{
    public static MediaInventoryService Create(
        DbContextOptions<AppDbContext> options,
        IMediaProbeRunner? probeRunner = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(options));
        var provider = services.BuildServiceProvider();

        return new MediaInventoryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            probeRunner ?? new FakeMediaProbeRunner(),
            NullLogger<MediaInventoryService>.Instance);
    }
}

// Records every probe; unknown paths are rejected like a non-media file.
internal sealed class FakeMediaProbeRunner : IMediaProbeRunner
{
    private readonly Dictionary<string, MediaProbeRun> results = new(StringComparer.Ordinal);
    private readonly List<string> calls = [];

    public MediaProbeRun DefaultResult { get; set; } =
        new(MediaProbeRunStatus.Failed, Error: "Invalid data found when processing input");

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    public void Returns(string path, string probeJson) =>
        Returns(path, new MediaProbeRun(MediaProbeRunStatus.Completed, probeJson));

    public void Returns(string path, MediaProbeRun run)
    {
        lock (calls)
        {
            results[Path.GetFullPath(path)] = run;
        }
    }

    public void ClearCalls()
    {
        lock (calls)
        {
            calls.Clear();
        }
    }

    // When set, every probe waits for it; lets a test hold a library scan in its running state.
    public Task? Gate { get; set; }

    public async Task<MediaProbeRun> ProbeAsync(string fullPath, CancellationToken cancellationToken)
    {
        MediaProbeRun result;
        lock (calls)
        {
            calls.Add(fullPath);
            result = results.GetValueOrDefault(fullPath, DefaultResult);
        }

        if (Gate is { } gate)
        {
            await gate.WaitAsync(cancellationToken);
        }

        return result;
    }
}

internal sealed class MediaInventoryFixture : IAsyncDisposable
{
    private int nextEpisodeNumber = 1;

    private MediaInventoryFixture(
        string tempRoot,
        DbContextOptions<AppDbContext> options,
        AppDbContext db,
        LibraryRoot root,
        Anime anime)
    {
        TempRoot = tempRoot;
        Options = options;
        Db = db;
        Root = root;
        Anime = anime;
        Inventory = MediaInventoryTestSupport.Create(options, Runner);
    }

    public string TempRoot { get; }
    public DbContextOptions<AppDbContext> Options { get; }
    public AppDbContext Db { get; }
    public LibraryRoot Root { get; }
    public Anime Anime { get; }
    public FakeMediaProbeRunner Runner { get; } = new();
    public MediaInventoryService Inventory { get; }

    public static async Task<MediaInventoryFixture> CreateAsync()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"jularr-inventory-{Guid.NewGuid():N}");
        var libraryPath = Path.Combine(tempRoot, "anime");
        Directory.CreateDirectory(libraryPath);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(tempRoot, "jularr.db")};Foreign Keys=True")
            .Options;
        var db = new AppDbContext(options);
        await DatabaseMigrationBridge.UpgradeAsync(db);

        var root = new LibraryRoot { Name = "Anime", Path = libraryPath };
        var anime = new Anime { Key = "inventory", Title = "Inventory" };
        db.AddRange(root, anime);
        await db.SaveChangesAsync();

        return new MediaInventoryFixture(tempRoot, options, db, root, anime);
    }

    /// <summary>Bridges the media's legacy Episode record to a Work, as the library does for every Anime episode, so the legacy plan route can find the Work to play it under.</summary>
    public async Task BridgeEpisodeAsync(MediaFile media)
    {
        var work = new Jularr.Web.Features.MediaCore.Work { MediaType = Jularr.Web.Features.MediaCore.WorkMediaType.Series, CanonicalTitle = "Bridged" };
        Db.Works.Add(work);
        await Db.SaveChangesAsync();
        Db.WorkSourceLinks.Add(new Jularr.Web.Features.MediaCore.WorkSourceLink { WorkId = work.Id, SourceKind = Jularr.Web.Features.MediaCore.WorkSourceKind.Episode, SourceId = media.EpisodeId!.Value });
        await Db.SaveChangesAsync();
    }

    public async Task<MediaFile> AddMediaAsync(string fileName, byte[] content)
    {
        var path = Path.GetFullPath(Path.Combine(Root.Path, fileName));
        await File.WriteAllBytesAsync(path, content);
        var info = new FileInfo(path);

        var episode = new Episode
        {
            AnimeId = Anime.Id,
            SeasonNumber = 1,
            Number = nextEpisodeNumber++,
            Title = fileName
        };
        var media = new MediaFile
        {
            LibraryRootId = Root.Id,
            EpisodeId = episode.Id,
            Path = path,
            SizeBytes = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc
        };
        Db.AddRange(episode, media);
        await Db.SaveChangesAsync();
        return media;
    }

    // Mirrors what the library scanner records after observing a changed file.
    public async Task RecordObservedIdentityAsync(MediaFile media)
    {
        var info = new FileInfo(media.Path);
        media.SizeBytes = info.Length;
        media.LastWriteTimeUtc = info.LastWriteTimeUtc;
        await Db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();

        if (Directory.Exists(TempRoot))
        {
            Directory.Delete(TempRoot, recursive: true);
        }
    }
}

internal static class MediaProbeFixtures
{
    public const string HevcTenBitHdrMultiAudio = """
    {
      "streams": [
        {
          "index": 0,
          "codec_name": "hevc",
          "profile": "Main 10",
          "codec_type": "video",
          "codec_tag_string": "[0][0][0][0]",
          "width": 3840,
          "height": 2160,
          "pix_fmt": "yuv420p10le",
          "color_transfer": "smpte2084",
          "color_primaries": "bt2020",
          "disposition": { "default": 1, "forced": 0, "attached_pic": 0 }
        },
        {
          "index": 1,
          "codec_name": "eac3",
          "codec_type": "audio",
          "channels": 6,
          "channel_layout": "5.1(side)",
          "disposition": { "default": 1, "forced": 0 },
          "tags": { "language": "jpn", "title": "Japanese 5.1" }
        },
        {
          "index": 2,
          "codec_name": "aac",
          "codec_type": "audio",
          "channels": 2,
          "channel_layout": "stereo",
          "disposition": { "default": 0, "forced": 0 },
          "tags": { "language": "eng" }
        },
        {
          "index": 3,
          "codec_name": "ass",
          "codec_type": "subtitle",
          "disposition": { "default": 1, "forced": 0 },
          "tags": { "language": "jpn", "title": "Full Dialogue" }
        },
        {
          "index": 4,
          "codec_name": "hdmv_pgs_subtitle",
          "codec_type": "subtitle",
          "disposition": { "default": 0, "forced": 1 },
          "tags": { "LANGUAGE": "eng", "title": "Signs" }
        },
        {
          "index": 5,
          "codec_name": "ttf",
          "codec_type": "attachment",
          "tags": { "filename": "font.ttf", "mimetype": "font/ttf" }
        }
      ],
      "format": {
        "format_name": "matroska,webm",
        "duration": "1420.500000"
      }
    }
    """;

    public const string H264Stereo = """
    {
      "streams": [
        {
          "index": 0,
          "codec_name": "h264",
          "profile": "High",
          "codec_type": "video",
          "width": 1920,
          "height": 1080,
          "pix_fmt": "yuv420p",
          "disposition": { "default": 1, "forced": 0 }
        },
        {
          "index": 1,
          "codec_name": "aac",
          "codec_type": "audio",
          "channels": 2,
          "channel_layout": "stereo",
          "disposition": { "default": 1, "forced": 0 },
          "tags": { "language": "jpn" }
        }
      ],
      "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "1440.0" }
    }
    """;
}
