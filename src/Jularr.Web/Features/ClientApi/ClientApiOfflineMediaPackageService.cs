using System.Security.Cryptography;
using System.Text;
using Jularr.Web.Data;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Manga;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Jularr.Web.Features.ClientApi;

/// <summary>
/// Authoritative manifest and byte resolver for explicit PWA media packages.  It intentionally
/// exposes stable resource ids rather than host file paths; every content request is resolved again
/// from the database and the current filesystem state.
/// </summary>
public sealed class ClientApiOfflineMediaPackageService(
    AppDbContext db,
    OfflinePortableRenditionService renditions,
    IInstanceModuleService? instanceModules = null)
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".webm", ".mkv", ".mov", ".avi"
    };
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".ogg", ".opus", ".wav", ".flac"
    };

    public async Task<ClientOfflineMediaPackageManifest?> GetManifestAsync(
        string kind,
        Guid id,
        CancellationToken cancellationToken)
    {
        var sources = await GetSourcesAsync(kind, id, cancellationToken);
        if (sources is null || sources.Count == 0)
        {
            return null;
        }

        var resources = new List<ClientOfflineMediaPackageResource>(sources.Count);
        foreach (var source in sources)
        {
            var rendition = await ResolveRenditionAsync(source, cancellationToken);
            var file = rendition is null ? null : new FileInfo(rendition.Path);
            if (file is not { Exists: true })
            {
                continue;
            }

            resources.Add(new(
                source.Id,
                source.Type,
                source.Name,
                rendition!.ContentType,
                file.Length,
                EntityTag(file),
                await HashAsync(file.FullName, cancellationToken),
                ClientApiOfflineMediaRoutes.Content(kind, id, source.Id)));
        }

        return resources.Count == 0
            ? null
            : new(ClientApiContract.ApiVersion, PackageId(kind, id), kind, id, sources[0].Title, DateTime.UtcNow, resources);
    }

    public async Task<ClientOfflineMediaFile?> ResolveAsync(
        string kind,
        Guid id,
        string resourceId,
        CancellationToken cancellationToken)
    {
        var sources = await GetSourcesAsync(kind, id, cancellationToken);
        var source = sources?.SingleOrDefault(x => x.Id == resourceId);
        if (source is null)
        {
            return null;
        }

        var rendition = await ResolveRenditionAsync(source, cancellationToken);
        var file = rendition is null ? null : new FileInfo(rendition.Path);
        return file is { Exists: true }
            ? new(file.FullName, rendition!.ContentType, file.LastWriteTimeUtc, EntityTag(file))
            : null;
    }

    private async Task<OfflinePortableRendition?> ResolveRenditionAsync(OfflineSource source, CancellationToken cancellationToken)
    {
        if (source.Type is not ("video" or "audio" or "subtitle"))
        {
            return File.Exists(source.Path) ? new(source.Path, source.ContentType, IsTemporary: false) : null;
        }
        return await renditions.GetAsync(source.Path, source.Type, cancellationToken);
    }

    private async Task<IReadOnlyList<OfflineSource>?> GetSourcesAsync(
        string kind,
        Guid id,
        CancellationToken cancellationToken)
    {
        var normalized = kind.ToLowerInvariant();
        if (instanceModules is not null && OfflineInstanceModule(normalized) is { } module)
        {
            // A video package is the media itself for playing, so a manager-only instance serves none; books, manga and audiobooks keep
            // only their own module switch. The kind list lives in OfflineInstanceModule alone.
            var video = module is InstanceModule.Anime or InstanceModule.Movie or InstanceModule.Tv;
            if (!await instanceModules.IsEnabledAsync(module, cancellationToken) || video && !await instanceModules.IsEnabledAsync(InstanceModule.Playback, cancellationToken))
            {
                return null;
            }
        }

        return normalized switch
        {
            "episode" => await EpisodeSourcesAsync(id, cancellationToken),
            "audiobook" => await AudiobookSourcesAsync(id, cancellationToken),
            "movie" => await FolderSourcesAsync("movie", id, cancellationToken),
            "tv" or "series" => await FolderSourcesAsync("tv", id, cancellationToken),
            "manga" => await MangaSourcesAsync(id, cancellationToken),
            "book" => await BookSourcesAsync(id, cancellationToken),
            _ => null
        };
    }

    private static InstanceModule? OfflineInstanceModule(string kind) =>
        kind switch
        {
            "episode" => InstanceModule.Anime,
            "audiobook" => InstanceModule.Audiobook,
            "movie" => InstanceModule.Movie,
            "tv" or "series" => InstanceModule.Tv,
            "manga" => InstanceModule.Manga,
            "book" => InstanceModule.Book,
            _ => null
        };

    private async Task<IReadOnlyList<OfflineSource>?> EpisodeSourcesAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await (from episode in db.Episodes.AsNoTracking()
                         join media in db.MediaFiles.AsNoTracking() on episode.Id equals media.EpisodeId
                         where episode.Id == id
                         orderby media.Path
                         select new { episode.Title, media.Path })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        var sidecars = await db.SubtitleTracks.AsNoTracking()
            .Where(track => track.EpisodeId == id)
            .OrderBy(track => track.Language).ThenBy(track => track.Path)
            .ToListAsync(cancellationToken);
        return [
            new OfflineSource("video-0", row.Title, Path.GetFileName(row.Path), row.Path, "video", ContentType(row.Path)),
            .. sidecars.Select(track => new OfflineSource(
                $"subtitle-{track.Id:D}", row.Title, Path.GetFileName(track.Path), track.Path,
                "subtitle", "text/vtt"))
        ];
    }

    private async Task<IReadOnlyList<OfflineSource>?> AudiobookSourcesAsync(Guid id, CancellationToken cancellationToken)
    {
        var book = await db.Audiobooks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (book is null) return null;
        var files = await db.AudiobookFiles.AsNoTracking()
            .Where(x => x.AudiobookId == id)
            .OrderBy(x => x.FileName)
            .ToListAsync(cancellationToken);
        return files.Select((file, index) => new OfflineSource(
            $"audio-{index}", book.Title, file.FileName, file.StoragePath, "audio", ContentType(file.StoragePath))).ToArray();
    }

    private async Task<IReadOnlyList<OfflineSource>?> FolderSourcesAsync(string kind, Guid id, CancellationToken cancellationToken)
    {
        string? title;
        string? folder;
        if (kind == "movie")
        {
            var movie = await db.Movies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (movie is null) return null;
            title = movie.Title; folder = movie.LibraryPath;
        }
        else
        {
            var series = await db.TvSeries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (series is null) return null;
            title = series.Title; folder = series.LibraryPath;
        }
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return [];
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(path => VideoExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return files.Select((path, index) => new OfflineSource(
            $"video-{index}", title!, Path.GetFileName(path), path, "video", ContentType(path))).ToArray();
    }

    private async Task<IReadOnlyList<OfflineSource>?> MangaSourcesAsync(Guid chapterId, CancellationToken cancellationToken)
    {
        var repository = new MangaRepository(db);
        var chapter = await repository.GetChapterAsync(chapterId, cancellationToken);
        if (chapter is null) return null;
        var pages = new List<OfflineSource>();
        for (var pageIndex = 0; pageIndex < chapter.PageCount; pageIndex++)
        {
            var page = await repository.GetPageAsync(chapterId, pageIndex, cancellationToken);
            if (page is not null) pages.Add(new($"page-{pageIndex}", chapter.SeriesTitle,
                $"{pageIndex + 1}", page.CachedPath, "image", page.MimeType));
        }
        return pages;
    }

    private async Task<IReadOnlyList<OfflineSource>?> BookSourcesAsync(Guid workId, CancellationToken cancellationToken)
    {
        var files = await (from edition in db.BookEditions.AsNoTracking()
                           join file in db.BookFiles.AsNoTracking() on edition.Id equals file.EditionId
                           where edition.WorkId == workId && file.StoragePath != null
                           orderby file.IsPrimary descending, file.FileName
                           select new { edition.Title, file })
            .ToListAsync(cancellationToken);
        return files.Select((row, index) => new OfflineSource($"document-{index}", row.Title ?? "Book",
            row.file.FileName, row.file.StoragePath!, "document", row.file.MediaType)).ToArray();
    }

    private static string PackageId(string kind, Guid id) => $"{kind.ToLowerInvariant()}:{id:D}";
    private static string EntityTag(FileInfo file) => ClientApiOfflineContract.EntityTag(file.Length, file.LastWriteTimeUtc);

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4", ".webm" => "video/webm", ".mkv" => "video/x-matroska",
        ".mov" => "video/quicktime", ".mp3" => "audio/mpeg", ".m4a" or ".m4b" => "audio/mp4",
        ".ogg" or ".opus" => "audio/ogg", ".wav" => "audio/wav", ".flac" => "audio/flac",
        ".epub" => "application/epub+zip", ".pdf" => "application/pdf", _ => "application/octet-stream"
    };

    private sealed record OfflineSource(string Id, string Title, string Name, string Path, string Type, string ContentType);
}

public sealed record ClientOfflineMediaPackageManifest(
    int ApiVersion, string Id, string Kind, Guid MediaId, string Title, DateTime IssuedAtUtc,
    IReadOnlyList<ClientOfflineMediaPackageResource> Resources);

public sealed record ClientOfflineMediaPackageResource(
    string Id, string Type, string Name, string MimeType, long SizeBytes, string ETag, string Sha256, string ContentUrl);

public sealed record ClientOfflineMediaFile(string Path, string ContentType, DateTime LastWriteTimeUtc, string ETag);

public static class ClientApiOfflineMediaRoutes
{
    public static string Manifest(string kind, Guid id) => $"{ClientApiContract.BasePath}/offline-media/{Uri.EscapeDataString(kind)}/{id:D}/manifest";
    public static string Content(string kind, Guid id, string resourceId) =>
        $"{ClientApiContract.BasePath}/offline-media/{Uri.EscapeDataString(kind)}/{id:D}/resources/{Uri.EscapeDataString(resourceId)}";
}
