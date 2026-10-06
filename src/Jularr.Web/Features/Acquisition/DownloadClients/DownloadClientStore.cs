using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Web.Features.Acquisition.DownloadClients;

/// <summary>
/// The one canonical download client list. SABnzbd connections are all
/// entries here; there is no other download client configuration path.
/// Jularr is usenet-only, so no entry is ever a torrent client. Each
/// entry's secret is protected at rest.
/// </summary>
public sealed class DownloadClientStore
{
    public const string FileName = "download-clients.json";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IDataProtector protector;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storePath;

    public DownloadClientStore(IDataProtectionProvider dataProtectionProvider)
        : this(dataProtectionProvider, new DirectoryInfo("/data/acquisition"))
    {
    }

    public DownloadClientStore(
        IDataProtectionProvider dataProtectionProvider,
        DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(directory);

        protector = dataProtectionProvider.CreateProtector(
            "Jularr.Acquisition.DownloadClients.Secret.v1");
        storePath = Path.Combine(directory.FullName, FileName);
    }

    public bool Exists => File.Exists(storePath);

    public async Task<IReadOnlyList<DownloadClientEntry>> LoadAllAsync(
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

    public async Task<DownloadClientEntry?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var all = await LoadAllAsync(cancellationToken);
        return all.FirstOrDefault(entry => entry.Id == id);
    }

    public async Task SaveAsync(
        DownloadClientEntry entry,
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

    public static DownloadClientEntry NormalizeAndValidate(DownloadClientEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new ArgumentException("Download client name is required.", nameof(entry));
        }

        var baseUrl = entry.Settings.BaseUrl?.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "Download client Base URL must be an absolute HTTP(S) URL without credentials, query or fragment.",
                nameof(entry));
        }

        return entry with
        {
            Name = entry.Name.Trim(),
            Settings = entry.Settings with { BaseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/') }
        };
    }

    private async Task<IReadOnlyList<DownloadClientEntry>> LoadUnlockedAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(storePath))
        {
            return [];
        }

        PersistedDownloadClientEntry[]? persisted;
        try
        {
            var json = await File.ReadAllTextAsync(storePath, cancellationToken);
            persisted = JsonSerializer.Deserialize<PersistedDownloadClientEntry[]>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Download client settings contain invalid JSON.", exception);
        }

        if (persisted is null)
        {
            return [];
        }

        var result = new List<DownloadClientEntry>();
        foreach (var item in persisted)
        {
            var secret = string.IsNullOrWhiteSpace(item.ProtectedSecret)
                ? null
                : protector.Unprotect(item.ProtectedSecret);

            result.Add(
                new DownloadClientEntry(
                    item.Id,
                    item.Name,
                    item.Type,
                    item.Enabled,
                    item.Priority,
                    new DownloadClientSettings(item.BaseUrl, ReadCategories(item)),
                    secret));
        }

        return result;
    }

    private async Task WriteUnlockedAsync(
        IReadOnlyList<DownloadClientEntry> entries,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(storePath)
            ?? throw new InvalidOperationException("Download client settings path has no directory.");
        Directory.CreateDirectory(directory);

        var persisted = entries
            .Select(entry => new PersistedDownloadClientEntry(
                entry.Id,
                entry.Name,
                entry.Type,
                entry.Enabled,
                entry.Priority,
                entry.Settings.BaseUrl,
                entry.Settings.Categories.ToDictionary(
                    pair => AcquisitionAccessNames.Kind(pair.Key),
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase),
                null,
                null,
                entry.Secret is null ? null : protector.Protect(entry.Secret)))
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

    private static IReadOnlyDictionary<MediaAcquisitionKind, string?> ReadCategories(
        PersistedDownloadClientEntry persisted)
    {
        var categories = new Dictionary<MediaAcquisitionKind, string?>();
        if (persisted.Categories is not null)
        {
            foreach (var (name, category) in persisted.Categories)
            {
                try
                {
                    categories[AcquisitionAccessNames.ParseKind(name)] = category;
                }
                catch (ArgumentException)
                {
                    // Unknown future media kinds are intentionally ignored by this version.
                }
            }
        }

        // One-time shape migration for pre-#389 client settings. Writing this entry next persists
        // the canonical map and removes these legacy fields; no runtime fallback is retained.
        if (!categories.ContainsKey(MediaAcquisitionKind.Book))
        {
            categories[MediaAcquisitionKind.Book] = persisted.BooksCategory;
        }

        if (!categories.ContainsKey(MediaAcquisitionKind.Anime))
        {
            categories[MediaAcquisitionKind.Anime] = persisted.AnimeCategory;
        }

        return categories;
    }

    private sealed record PersistedDownloadClientEntry(
        Guid Id,
        string Name,
        DownloadClientType Type,
        bool Enabled,
        int Priority,
        string BaseUrl,
        Dictionary<string, string?>? Categories,
        string? BooksCategory,
        string? AnimeCategory,
        string? ProtectedSecret);
}
