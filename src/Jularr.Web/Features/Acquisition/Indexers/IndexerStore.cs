using System.Text.Json;
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
                : settings.BookCategories.Where(value => value > 0).Distinct().Order().ToArray()
        };

        return entry with
        {
            Name = entry.Name.Trim(),
            ApiKey = entry.ApiKey.Trim(),
            Settings = normalizedSettings
        };
    }

    private async Task<IReadOnlyList<IndexerEntry>> LoadUnlockedAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(storePath))
        {
            return [];
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
            var apiKey = protector.Unprotect(item.ProtectedApiKey);
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
                        item.BookCategories),
                    apiKey));
        }

        return result;
    }

    private async Task WriteUnlockedAsync(
        IReadOnlyList<IndexerEntry> entries,
        CancellationToken cancellationToken)
    {
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
                entry.Settings.BookCategories))
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
        int[]? BookCategories = null);
}
