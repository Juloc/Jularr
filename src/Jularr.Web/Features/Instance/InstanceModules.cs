using System.Text.Json;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Instance;

/// <summary>
/// Server-wide feature switches. These are stronger than profile settings and permissions:
/// a disabled module does not expose UI/routes and must not start new background work.
/// Existing data is retained so re-enabling a module restores its previous state.
/// </summary>
public enum InstanceModule
{
    Anime = 1,
    Movie = 2,
    Tv = 3,
    Manga = 4,
    Novel = 5,
    Book = 6,
    Audiobook = 7,
    Learning = 8,
    Acquisition = 9,
    Tracking = 10,

    /// <summary>
    /// Whether this instance plays media itself. Off is the manager-only mode (discovery, requests, monitoring and
    /// acquisition without a Jularr player): no Watch page, no player/plan/progress/stream API and no play action.
    /// </summary>
    Playback = 11,

    /// <summary>Music (albums, artists) acquisition and library management.</summary>
    Music = 12
}

public sealed record InstanceModuleSettings(
    IReadOnlyDictionary<InstanceModule, bool> Modules)
{
    public static InstanceModuleSettings Default { get; } = new(
        Enum.GetValues<InstanceModule>()
            .ToDictionary(module => module, _ => true));

    public bool IsEnabled(InstanceModule module) =>
        !Modules.TryGetValue(module, out var enabled) || enabled;

    public InstanceModuleSettings With(InstanceModule module, bool enabled)
    {
        var modules = Modules.ToDictionary(pair => pair.Key, pair => pair.Value);
        modules[module] = enabled;
        return new InstanceModuleSettings(modules);
    }
}

public static class InstanceModuleMedia
{
    public static InstanceModule For(WorkMediaType mediaType) =>
        mediaType switch
        {
            WorkMediaType.Movie => InstanceModule.Movie,
            WorkMediaType.Series => InstanceModule.Tv,
            WorkMediaType.Anime => InstanceModule.Anime,
            WorkMediaType.Book => InstanceModule.Book,
            WorkMediaType.Manga => InstanceModule.Manga,
            WorkMediaType.LightNovel => InstanceModule.Novel,
            WorkMediaType.Music => InstanceModule.Music,
            _ => throw new ArgumentOutOfRangeException(nameof(mediaType))
        };

    /// <summary>
    /// Whether the capability family represented by a canonical work type still has at least one
    /// enabled instance module. Audiobooks intentionally share the Book capability family.
    /// </summary>
    public static bool IsCapabilityFamilyEnabled(
        InstanceModuleSettings settings,
        WorkMediaType mediaType) =>
        mediaType == WorkMediaType.Book
            ? settings.IsEnabled(InstanceModule.Book)
              || settings.IsEnabled(InstanceModule.Audiobook)
            : settings.IsEnabled(For(mediaType));
}

public interface IInstanceModuleService
{
    Task<InstanceModuleSettings> GetAsync(CancellationToken cancellationToken = default);

    Task<bool> IsEnabledAsync(
        InstanceModule module,
        CancellationToken cancellationToken = default);

    Task<InstanceModuleSettings> SetAsync(
        InstanceModule module,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<InstanceModuleSettings> SaveAsync(
        InstanceModuleSettings settings,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Canonical durable store for instance-level module switches.
/// Missing files and newly introduced modules default to enabled so upgrades preserve behavior.
/// </summary>
public sealed class InstanceModuleStore : IInstanceModuleService
{
    public const string FileName = "instance-modules.json";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path;
    private InstanceModuleSettings? cached;

    public InstanceModuleStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        path = Path.Combine(dataRoot, "system", FileName);
    }

    public async Task<InstanceModuleSettings> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            cached ??= await LoadUnlockedAsync(cancellationToken);
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> IsEnabledAsync(
        InstanceModule module,
        CancellationToken cancellationToken = default) =>
        (await GetAsync(cancellationToken)).IsEnabled(module);

    public async Task<InstanceModuleSettings> SetAsync(
        InstanceModule module,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = cached ??= await LoadUnlockedAsync(cancellationToken);
            var updated = current.With(module, enabled);
            await SaveUnlockedAsync(updated, cancellationToken);
            cached = updated;
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<InstanceModuleSettings> SaveAsync(
        InstanceModuleSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var normalized = new InstanceModuleSettings(
                Enum.GetValues<InstanceModule>()
                    .ToDictionary(
                        module => module,
                        settings.IsEnabled));
            await SaveUnlockedAsync(normalized, cancellationToken);
            cached = normalized;
            return normalized;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<InstanceModuleSettings> LoadUnlockedAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return InstanceModuleSettings.Default;
        }

        PersistedSettings? persisted;
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            persisted = JsonSerializer.Deserialize<PersistedSettings>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Instance module settings '{path}' are invalid JSON.",
                exception);
        }

        var modules = InstanceModuleSettings.Default.Modules
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        foreach (var (name, enabled) in persisted?.Modules ?? [])
        {
            if (Enum.TryParse<InstanceModule>(name, ignoreCase: true, out var module))
            {
                modules[module] = enabled;
            }
        }

        return new InstanceModuleSettings(modules);
    }

    private async Task SaveUnlockedAsync(
        InstanceModuleSettings settings,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var persisted = new PersistedSettings(
            settings.Modules.ToDictionary(
                pair => pair.Key.ToString(),
                pair => pair.Value,
                StringComparer.Ordinal));

        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(persisted, JsonOptions),
                cancellationToken);
            SetPrivateFileMode(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void SetPrivateFileMode(string filePath)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                filePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PersistedSettings(Dictionary<string, bool> Modules);
}

/// <summary>
/// Runtime route gates implemented so far. Add a module here only after its whole vertical
/// (navigation, routes, services and background work) has adopted the same instance switch.
/// </summary>
public static class InstanceModuleRoutes
{
    private static readonly IReadOnlyDictionary<InstanceModule, string[]> Roots =
        new Dictionary<InstanceModule, string[]>
        {
            [InstanceModule.Anime] =
            [
                "/Acquisition",
                "/Admin/Media",
                "/Admin/Sonarr",
                "/Settings/Naming",
                "/Settings/Sonarr",
                "/Settings/SonarrMigration",
                "/api/acquisition/v1"
            ],
            [InstanceModule.Manga] =
            [
                "/Discover/MangaImport"
            ],
            [InstanceModule.Novel] =
            [
                "/Admin/ReadingSources"
            ],
            [InstanceModule.Book] =
            [
                "/Settings/Books"
            ],
            [InstanceModule.Acquisition] =
            [
                "/Acquisition",
                "/Requests",
                "/Admin/Requests",
                "/Admin/Wanted",
                "/Admin/Usenet",
                "/Admin/ReadingSources",
                "/Admin/Sonarr",
                "/Settings/Acquisition",
                "/Settings/Naming",
                "/Settings/ReadingNaming",
                "/Settings/Indexers",
                "/Settings/DownloadClients",
                "/Settings/Sonarr",
                "/Settings/SonarrMigration",
                "/api/acquisition/v1"
            ],
            [InstanceModule.Learning] =
            [
                "/Learn",
                "/Kana",
                "/Statistics",
                "/Settings/Learning",
                "/Settings/LearningCourses",
                "/Settings/LearningScope",
                "/api/language-inspector"
            ],
            [InstanceModule.Tracking] =
            [
                "/Settings/AniList"
            ],
            // Only what serves or locates playable bytes, plans or starts playback, or spawns ffmpeg: manager-only clients keep episode
            // metadata, flow, progress and watched marks, cues and segments, storage availability and offline books/manga.
            [InstanceModule.Playback] =
            [
                "/Library/Watch",
                "/Library/Episode",
                "/api/client/v1/video/player",
                "/api/client/v1/video/playback-plan",
                "/api/client/v1/video/playback-intents",
                "/api/client/v1/video/progress",
                "/api/client/v1/video/subtitle-tracks",
                "/api/client/v1/stream-sessions",
                "/api/client/v1/media/*/content",
                "/api/client/v1/media/*/trickplay",
                "/api/client/v1/episodes/*/player",
                "/api/client/v1/episodes/*/hls",
                "/api/client/v1/episodes/*/fallback",
                "/api/client/v1/episodes/*/playback-plan",
                "/api/client/v1/episodes/*/offline-download",
                "/api/client/v1/episodes/*/trickplay",
                "/api/client/v1/episodes/*/subtitle-tracks",
                "/api/client/v1/offline/media"
            ]
        };

    public static IReadOnlyList<InstanceModule> Resolve(PathString path) =>
        Roots
            .Where(pair => pair.Value.Any(root => Matches(path, root)))
            .Select(pair => pair.Key)
            .Distinct()
            .ToArray();

    /// <summary>A root is a path prefix; a <c>*</c> segment in it matches any one segment (an id), so a route can be gated by what follows the id.</summary>
    private static bool Matches(PathString path, string root)
    {
        if (!root.Contains('*'))
        {
            return path.StartsWithSegments(new PathString(root), StringComparison.OrdinalIgnoreCase);
        }

        var wanted = root.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var actual = (path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        return actual.Length >= wanted.Length
            && wanted.Select((segment, index) => segment == "*" || string.Equals(segment, actual[index], StringComparison.OrdinalIgnoreCase)).All(matches => matches);
    }

    public static bool TryResolve(PathString path, out InstanceModule module)
    {
        var resolved = Resolve(path);
        if (resolved.Count > 0)
        {
            module = resolved[0];
            return true;
        }

        module = default;
        return false;
    }
}

public static class InstanceModuleGateExtensions
{
    /// <summary>
    /// Instance module switches are stronger than profile settings: a route of a disabled module answers 404 immediately, like a
    /// hidden media type. Background and service gates use the same <see cref="IInstanceModuleService"/>.
    /// </summary>
    public static IApplicationBuilder UseInstanceModuleGates(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var requiredModules = InstanceModuleRoutes.Resolve(context.Request.Path);
            if (requiredModules.Count > 0)
            {
                var modules = context.RequestServices.GetRequiredService<IInstanceModuleService>();
                var settings = await modules.GetAsync(context.RequestAborted);
                if (requiredModules.Any(module => !settings.IsEnabled(module)))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
            }

            await next();
        });
}
