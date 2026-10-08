using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Web.Features.Acquisition.Indexers;

/// <summary>
/// The one canonical indexer list. Prowlarr and direct Newznab connections
/// are all entries here; there is no other indexer configuration path.
/// Jularr is usenet-only, so no entry ever carries a torrent protocol.
/// Each entry's API key is protected at rest.
/// </summary>
public sealed class IndexerStore
{
    public const string FileName = "indexers.json";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IDataProtector protector;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storePath;

    // Every indexer search and health check loads the entries: the decrypted entries are kept until the file changes (its write time and length).
    private (DateTime WrittenAt, long Length, IReadOnlyList<IndexerEntry> Entries)? cached;

    public IndexerStore(IDataProtectionProvider dataProtectionProvider)
        : this(dataProtectionProvider, new DirectoryInfo("/data/acquisition"))
    {
    }

    public IndexerStore(
        IDataProtectionProvider dataProtectionProvider,
        DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(directory);

        protector = dataProtectionProvider.CreateProtector(
            "Jularr.Acquisition.Indexers.ApiKey.v1");
        storePath = Path.Combine(directory.FullName, FileName);
    }

    public bool Exists => File.Exists(storePath);

    public async Task<IReadOnlyList<IndexerEntry>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadUnlockedAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IndexerEntry?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var all = await LoadAllAsync(cancellationToken);
        return all.FirstOrDefault(entry => entry.Id == id);
    }

    /// <summary>
    /// Stores the capabilities an indexer reported when it was tested. Only that one setting changes, so an edit made while the test ran
    /// is not overwritten by a stale copy of the entry.
    /// </summary>
    public async Task UpdateCapabilitiesAsync(Guid id, IndexerCapabilities capabilities, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var all = (await LoadUnlockedAsync(cancellationToken)).ToList();
            var index = all.FindIndex(item => item.Id == id);
            if (index >= 0)
            {
                all[index] = all[index] with { Settings = all[index].Settings with { Capabilities = capabilities } };
                await WriteUnlockedAsync(all, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Adds or replaces the entry with the same Id.</summary>
    public async Task SaveAsync(
        IndexerEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var normalized = NormalizeAndValidate(entry);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var all = (await LoadUnlockedAsync(cancellationToken)).ToList();
            var index = all.FindIndex(item => item.Id == normalized.Id);
            if (index >= 0)
            {
                all[index] = normalized;
            }
            else
            {
                all.Add(normalized);
            }

            await WriteUnlockedAsync(all, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var all = (await LoadUnlockedAsync(cancellationToken)).ToList();
            var removed = all.RemoveAll(item => item.Id == id) > 0;
            if (removed)
            {
                await WriteUnlockedAsync(all, cancellationToken);
            }

            return removed;
        }
        finally
        {
            gate.Release();
        }
    }

    public static IndexerEntry NormalizeAndValidate(IndexerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new ArgumentException("Indexer name is required.", nameof(entry));
        }

        if (string.IsNullOrWhiteSpace(entry.ApiKey))
        {
            throw new ArgumentException("Indexer API key is required.", nameof(entry));
        }

        var settings = entry.Settings;
        if (!Uri.TryCreate(settings.BaseUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "Indexer Base URL must be an absolute HTTP(S) URL without credentials, query or fragment.",
                nameof(entry));
        }

        if (settings.SearchLimit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(entry),
                "Indexer search limit must be between 1 and 1000.");
        }

        var normalizedSettings = settings with
        {
            BaseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
            Categories = (settings.Categories ?? []).Where(value => value > 0).Distinct().Order().ToArray(),
            IndexerIds = (settings.IndexerIds ?? []).Where(value => value > 0).Distinct().Order().ToArray(),
            BookCategories = settings.BookCategories is null
                ? null
                : settings.BookCategories.Where(value => value > 0).Distinct().Order().ToArray(),
            CategoriesByKind = NormalizeKindCategories(settings.CategoriesByKind)
        };

        return entry with
        {
            Name = entry.Name.Trim(),
            ApiKey = entry.ApiKey.Trim(),
            Settings = normalizedSettings
        };
    }

    /// <summary>Keeps only the media types that exist and, for each, its positive distinct ids; a type left without any falls back to its default.</summary>
    private static Dictionary<string, int[]>? NormalizeKindCategories(Dictionary<string, int[]>? configured)
    {
        var known = Enum.GetValues<MediaAcquisitionKind>().Select(AcquisitionAccessNames.Kind).ToHashSet(StringComparer.Ordinal);
        var kept = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var (key, value) in configured ?? [])
        {
            var name = key.Trim().ToLowerInvariant();
            var ids = (value ?? []).Where(id => id > 0).Distinct().Order().ToArray();
            if (known.Contains(name) && ids.Length > 0)
            {
                kept[name] = ids;
            }
        }

        return kept.Count == 0 ? null : kept;
    }

    private async Task<IReadOnlyList<IndexerEntry>> LoadUnlockedAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(storePath))
        {
            return [];
        }

        var info = new FileInfo(storePath);
        if (cached is { } hit && hit.WrittenAt == info.LastWriteTimeUtc && hit.Length == info.Length)
        {
            return hit.Entries;
        }

        PersistedIndexerEntry[]? persisted;
        try
        {
            var json = await File.ReadAllTextAsync(storePath, cancellationToken);
            persisted = JsonSerializer.Deserialize<PersistedIndexerEntry[]>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Indexer settings contain invalid JSON.", exception);
        }

        if (persisted is null)
        {
            return [];
        }

        var result = new List<IndexerEntry>();
        foreach (var item in persisted)
        {
            var apiKey = ProtectedSecrets.Read(protector, item.ProtectedApiKey) ?? string.Empty;
            result.Add(
                new IndexerEntry(
                    item.Id,
                    item.Name,
                    item.Type,
                    item.Enabled,
                    item.Priority,
                    new IndexerSettings(
                        item.BaseUrl,
                        item.Categories ?? [],
                        item.IndexerIds ?? [],
                        item.SearchLimit,
                        item.BookCategories)
                    {
                        Capabilities = item.Capabilities,
                        AutomaticSearch = item.AutomaticSearch ?? true,
                        InteractiveSearch = item.InteractiveSearch ?? true,
                        MediaKinds = item.MediaKinds,
                        CategoriesByKind = item.CategoriesByKind
                    },
                    apiKey));
        }

        cached = (info.LastWriteTimeUtc, info.Length, result);
        return result;
    }

    private async Task WriteUnlockedAsync(
        IReadOnlyList<IndexerEntry> entries,
        CancellationToken cancellationToken)
    {
        cached = null;
        var directory = Path.GetDirectoryName(storePath)
            ?? throw new InvalidOperationException("Indexer settings path has no directory.");
        Directory.CreateDirectory(directory);

        var persisted = entries
            .Select(entry => new PersistedIndexerEntry(
                entry.Id,
                entry.Name,
                entry.Type,
                entry.Enabled,
                entry.Priority,
                entry.Settings.BaseUrl,
                entry.Settings.Categories,
                entry.Settings.IndexerIds,
                entry.Settings.SearchLimit,
                protector.Protect(entry.ApiKey),
                entry.Settings.BookCategories,
                entry.Settings.Capabilities,
                entry.Settings.AutomaticSearch,
                entry.Settings.InteractiveSearch,
                entry.Settings.MediaKinds,
                entry.Settings.CategoriesByKind))
            .ToArray();

        var temporaryPath = $"{storePath}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(persisted, JsonOptions),
                cancellationToken);
            SetPrivateFileMode(temporaryPath);
            File.Move(temporaryPath, storePath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PersistedIndexerEntry(
        Guid Id,
        string Name,
        IndexerType Type,
        bool Enabled,
        int Priority,
        string BaseUrl,
        int[]? Categories,
        int[]? IndexerIds,
        int SearchLimit,
        string ProtectedApiKey,
        int[]? BookCategories = null,
        IndexerCapabilities? Capabilities = null,
        bool? AutomaticSearch = null,
        bool? InteractiveSearch = null,
        MediaAcquisitionKind[]? MediaKinds = null,
        Dictionary<string, int[]>? CategoriesByKind = null);
}
