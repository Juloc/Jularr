using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Web.Features.Acquisition.Sabnzbd;

/// <summary>
/// The canonical persisted SABnzbd configuration shared by Books and Anime.
/// The API key is protected at rest with ASP.NET Core Data Protection.
/// </summary>
public sealed class SabnzbdSettingsStore
{
    public const string FileName = "sabnzbd.json";

    private const int MaxCategoryLength = 80;
    private const int MaxApiKeyLength = 512;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IDataProtector protector;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storePath;

    public SabnzbdSettingsStore(IDataProtectionProvider dataProtectionProvider)
        : this(
            dataProtectionProvider,
            new DirectoryInfo("/data/acquisition"))
    {
    }

    public SabnzbdSettingsStore(
        IDataProtectionProvider dataProtectionProvider,
        DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(directory);

        protector = dataProtectionProvider.CreateProtector(
            "Jularr.Acquisition.Sabnzbd.ApiKey.v1");
        storePath = Path.Combine(directory.FullName, FileName);
    }

    public string StorePath => storePath;

    public bool Exists => File.Exists(storePath);

    public async Task<SabnzbdStoredSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(storePath))
            {
                return SabnzbdStoredSettings.Empty;
            }

            PersistedSabnzbdSettings? persisted;
            try
            {
                var json = await File.ReadAllTextAsync(storePath, cancellationToken);
                persisted = JsonSerializer.Deserialize<PersistedSabnzbdSettings>(
                    json,
                    JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "SABnzbd settings contain invalid JSON.",
                    exception);
            }

            if (persisted is null)
            {
                return SabnzbdStoredSettings.Empty;
            }

            var apiKey = string.IsNullOrWhiteSpace(persisted.ProtectedApiKey)
                ? null
                : protector.Unprotect(persisted.ProtectedApiKey);

            return Normalize(
                new SabnzbdStoredSettings(
                    persisted.BaseUrl,
                    apiKey,
                    persisted.BooksCategory,
                    persisted.AnimeCategory));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        SabnzbdStoredSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(settings);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(storePath)
                ?? throw new InvalidOperationException(
                    "SABnzbd settings path has no directory.");
            Directory.CreateDirectory(directory);

            var persisted = new PersistedSabnzbdSettings(
                normalized.BaseUrl,
                normalized.ApiKey is null
                    ? null
                    : protector.Protect(normalized.ApiKey),
                normalized.BooksCategory,
                normalized.AnimeCategory);

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
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Trims and validates stored settings. Blank values become null.
    /// </summary>
    public static SabnzbdStoredSettings Normalize(SabnzbdStoredSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new SabnzbdStoredSettings(
            NormalizeBaseUrl(settings.BaseUrl),
            Clean(settings.ApiKey, MaxApiKeyLength, "API key"),
            Clean(settings.BooksCategory, MaxCategoryLength, "Books category"),
            Clean(settings.AnimeCategory, MaxCategoryLength, "Anime category"));
    }

    public static string? NormalizeBaseUrl(string? baseUrl)
    {
        var clean = baseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            return null;
        }

        if (!Uri.TryCreate(clean, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "SABnzbd URL must be an absolute HTTP(S) URL without credentials, query or fragment.",
                nameof(baseUrl));
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static string? Clean(
        string? value,
        int maxLength,
        string label)
    {
        var clean = value?.Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            return null;
        }

        if (clean.Length > maxLength)
        {
            throw new ArgumentException(
                $"SABnzbd {label} must not exceed {maxLength} characters.",
                nameof(value));
        }

        return clean;
    }

    private static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
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

    private sealed record PersistedSabnzbdSettings(
        string? BaseUrl,
        string? ProtectedApiKey,
        string? BooksCategory,
        string? AnimeCategory);
}
