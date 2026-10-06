using System.Text.Json.Serialization;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Media.Optimization;

namespace Jularr.Web.Features.Acquisition.Import;

/// <summary>
/// How a completed download's video (and its sidecars) is placed into the library. Hardlink and
/// Copy never remove the source; Move (the historical default) does. HardlinkOrCopy is an explicit
/// owner choice: try a hardlink first and fall back to a copy only when the source and the library
/// folder are on different filesystems. Plain Hardlink never falls back, so a cross-filesystem
/// import fails with a clear reason instead of silently copying.
/// </summary>
public enum ImportMode
{
    Move,
    Copy,
    Hardlink,
    HardlinkOrCopy
}

/// <summary>
/// Maps a path prefix as reported by an external system (the download client's completed-download
/// path, or a Sonarr series/episode/queue path) to the equivalent local prefix Jularr sees under
/// its own mounts. Entries are tried longest-prefix-first; a path with no matching entry is used
/// unchanged. Mappings belong to one media type (<see cref="MediaLibraryTarget.RemotePathMappings"/>):
/// each media type's completed-download folder can be mounted differently from what its download
/// client reports, and the Anime mappings also align Sonarr-observed paths with Jularr's own.
/// </summary>
public sealed record RemotePathMapping(string RemotePrefix, string LocalPrefix);

/// <summary>
/// The folders and path mappings of one media type: the final NAS library folder completed
/// downloads are placed in (with an optional import-mode override; null uses the global default),
/// the inbox folder the same importer scans for content Jularr did not download, and the remote
/// path mappings that translate the path an external system reports for this media type into the
/// path Jularr reads. Everything is optional.
/// </summary>
public sealed record MediaLibraryTarget(
    string? LibraryRoot = null,
    ImportMode? ImportMode = null,
    string? InboxRoot = null)
{
    public List<RemotePathMapping> RemotePathMappings { get; init; } = [];

    /// <summary>
    /// True when the target carries no folder and no path mapping and can be dropped from the
    /// store (an import mode only overrides the mode of a library folder).
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(LibraryRoot) &&
        string.IsNullOrWhiteSpace(InboxRoot) &&
        (RemotePathMappings is null || RemotePathMappings.Count == 0);
}

/// <summary>
/// The one canonical import-policy settings: default import mode, per-library-root overrides,
/// the folders and remote path mappings of each media type, the per-anime target root for new
/// imports and the post-import playback optimization. Stored at
/// <c>/data/acquisition/import-settings.json</c> next to the other acquisition stores.
/// </summary>
public sealed record AnimeImportSettingsState(
    int Version,
    ImportMode DefaultImportMode,
    Dictionary<Guid, ImportMode> RootImportModes)
{
    // Post-import step for imported video: a lossless container remux when it widens browser
    // Direct Play without losing anything (MediaContainerOptimizer). Off unless the owner opts in.
    public LosslessPlaybackOptimizationMode PlaybackOptimization { get; init; } = LosslessPlaybackOptimizationMode.Off;

    /// <summary>
    /// Library folder, import-mode override, inbox folder and remote path mappings per media
    /// type. Anime keeps using its library roots, so its entry only carries path mappings. A
    /// media type without a library folder imports completed downloads in place; without an inbox
    /// folder it has no inbox scan.
    /// </summary>
    public Dictionary<MediaAcquisitionKind, MediaLibraryTarget> MediaLibraries { get; init; } = [];


    public static AnimeImportSettingsState Empty() =>
        new(1, ImportMode.Move, []);

    public ImportMode ModeFor(Guid? rootId) =>
        rootId is { } id && RootImportModes.TryGetValue(id, out var mode) ? mode : DefaultImportMode;

    public MediaLibraryTarget? LibraryFor(MediaAcquisitionKind kind) =>
        MediaLibraries is not null &&
        MediaLibraries.TryGetValue(kind, out var target) &&
        !string.IsNullOrWhiteSpace(target.LibraryRoot)
            ? target
            : null;

    public ImportMode ModeFor(MediaAcquisitionKind kind) =>
        LibraryFor(kind)?.ImportMode ?? DefaultImportMode;

    /// <summary>The configured inbox folder of a media type, or null.</summary>
    public string? InboxFor(MediaAcquisitionKind kind) =>
        MediaLibraries is not null &&
        MediaLibraries.TryGetValue(kind, out var target) &&
        !string.IsNullOrWhiteSpace(target.InboxRoot)
            ? target.InboxRoot.Trim()
            : null;

    /// <summary>The folders of a media type (all unset when nothing is configured).</summary>
    public MediaLibraryTarget FoldersFor(MediaAcquisitionKind kind) =>
        MediaLibraries is not null && MediaLibraries.TryGetValue(kind, out var target)
            ? target
            : new MediaLibraryTarget();

    /// <summary>The remote path mappings of one media type, in configured order (none when unset).</summary>
    public IReadOnlyList<RemotePathMapping> RemotePathMappingsFor(MediaAcquisitionKind kind) =>
        MediaLibraries is not null &&
        MediaLibraries.TryGetValue(kind, out var target) &&
        target.RemotePathMappings is { } mappings
            ? mappings
            : [];

    /// <summary>How many remote path mappings are configured across every media type.</summary>
    [JsonIgnore]
    public int RemotePathMappingCount =>
        MediaLibraries?.Values.Sum(target => target.RemotePathMappings?.Count ?? 0) ?? 0;

    /// <summary>
    /// Replaces the remote path mappings of one media type and leaves everything else as is. A
    /// media type left without any folder or mapping is dropped.
    /// </summary>
    public AnimeImportSettingsState WithRemotePathMappings(
        MediaAcquisitionKind kind,
        IEnumerable<RemotePathMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        var libraries = new Dictionary<MediaAcquisitionKind, MediaLibraryTarget>(MediaLibraries ?? []);
        var target = (libraries.TryGetValue(kind, out var existing) ? existing : new MediaLibraryTarget()) with
        {
            RemotePathMappings = mappings.ToList()
        };
        if (target.IsEmpty)
        {
            libraries.Remove(kind);
        }
        else
        {
            libraries[kind] = target;
        }

        return this with { MediaLibraries = libraries };
    }

    /// <summary>
    /// Adds one mapping to a media type's remote path mappings. A mapping with the same remote
    /// prefix (ignoring case and surrounding whitespace) is replaced, so adding and previewing a
    /// mapping agree on the result.
    /// </summary>
    public AnimeImportSettingsState WithRemotePathMapping(MediaAcquisitionKind kind, RemotePathMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        var remote = mapping.RemotePrefix.Trim();
        return WithRemotePathMappings(
            kind,
            RemotePathMappingsFor(kind)
                .Where(existing => !existing.RemotePrefix.Equals(remote, StringComparison.OrdinalIgnoreCase))
                .Append(new RemotePathMapping(remote, mapping.LocalPrefix.Trim())));
    }

    /// <summary>
    /// The one path translation: rewrites <paramref name="path"/>, as an external system reports
    /// it for <paramref name="kind"/>, to the path Jularr reads, using the longest matching remote
    /// prefix among that media type's mappings. Comparison is ordinal-ignore-case and tolerant of
    /// '/' vs '\\'; a path with no matching entry is returned unchanged.
    /// </summary>
    public string TranslatePath(MediaAcquisitionKind kind, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        RemotePathMapping? best = null;
        foreach (var mapping in RemotePathMappingsFor(kind))
        {
            if (string.IsNullOrWhiteSpace(mapping.RemotePrefix))
            {
                continue;
            }

            if (!IsUnderOrEqual(path, mapping.RemotePrefix))
            {
                continue;
            }

            if (best is null || mapping.RemotePrefix.Length > best.RemotePrefix.Length)
            {
                best = mapping;
            }
        }

        if (best is null)
        {
            return path;
        }

        var remainder = path.Length > best.RemotePrefix.Length
            ? path[best.RemotePrefix.Length..]
            : "";
        var local = best.LocalPrefix.TrimEnd('/', '\\');
        return remainder.Length == 0 ? local : local + "/" + remainder.TrimStart('/', '\\').Replace('\\', '/');
    }

    private static bool IsUnderOrEqual(string path, string prefix)
    {
        var normalizedPath = path.Replace('\\', '/');
        var normalizedPrefix = prefix.Replace('\\', '/').TrimEnd('/');
        if (normalizedPath.Equals(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedPath.Length > normalizedPrefix.Length &&
               normalizedPath.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase) &&
               normalizedPath[normalizedPrefix.Length] == '/';
    }
}
