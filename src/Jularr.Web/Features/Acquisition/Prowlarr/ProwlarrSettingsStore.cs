using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Web.Features.Acquisition.Prowlarr;

public sealed class ProwlarrSettingsStore
{
    private const string FileName = "prowlarr.json";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IDataProtector protector;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storePath;

    public ProwlarrSettingsStore(IDataProtectionProvider dataProtectionProvider)
        : this(
            dataProtectionProvider,
            new DirectoryInfo("/data/acquisition"))
    {
    }

    public ProwlarrSettingsStore(
        IDataProtectionProvider dataProtectionProvider,
        DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(directory);

        protector = dataProtectionProvider.CreateProtector(
            "Jularr.Acquisition.Prowlarr.ApiKey.v1");
        storePath = Path.Combine(directory.FullName, FileName);
    }

    public bool Exists => File.Exists(storePath);

    public string StorePath => storePath;

    public async Task<ProwlarrConnection?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(storePath))
            {
                return null;
            }

            try
            {
                var json = await File.ReadAllTextAsync(storePath, cancellationToken);
                var persisted = JsonSerializer.Deserialize<PersistedProwlarrSettings>(
                    json,
                    JsonOptions);

                if (persisted is null)
                {
                    return null;
                }

                var settings = NormalizeAndValidate(
                    new ProwlarrSettings(
                        persisted.BaseUrl,
                        persisted.Categories ?? [],
                        persisted.IndexerIds ?? [],
                        persisted.SearchLimit));

                var apiKey = protector.Unprotect(persisted.ProtectedApiKey);
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidDataException("Stored Prowlarr API key is empty.");
                }

                return new ProwlarrConnection(settings, apiKey);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "Prowlarr settings contain invalid JSON.",
                    exception);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        ProwlarrConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var settings = NormalizeAndValidate(connection.Settings);

        if (string.IsNullOrWhiteSpace(connection.ApiKey))
        {
            throw new ArgumentException(
                "Prowlarr API key is required.",
                nameof(connection));
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(storePath)
                ?? throw new InvalidOperationException(
                    "Prowlarr settings path has no directory.");

            Directory.CreateDirectory(directory);

            var persisted = new PersistedProwlarrSettings(
                settings.BaseUrl,
                settings.Categories,
                settings.IndexerIds,
                settings.SearchLimit,
                protector.Protect(connection.ApiKey.Trim()));

            var temporaryPath = $"{storePath}.tmp-{Guid.NewGuid():N}";
            try
            {
                var json = JsonSerializer.Serialize(persisted, JsonOptions);
                await File.WriteAllTextAsync(
                    temporaryPath,
                    json,
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

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            TryDelete(storePath);
        }
        finally
        {
            gate.Release();
        }
    }

    public static ProwlarrSettings NormalizeAndValidate(ProwlarrSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!Uri.TryCreate(settings.BaseUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "Prowlarr Base URL must be an absolute HTTP(S) URL without credentials, query or fragment.",
                nameof(settings));
        }

        if (settings.SearchLimit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "Prowlarr search limit must be between 1 and 1000.");
        }

        var categories = (settings.Categories ?? [])
            .Where(value => value > 0)
            .Distinct()
            .Order()
            .ToArray();

        var indexerIds = (settings.IndexerIds ?? [])
            .Where(value => value > 0)
            .Distinct()
            .Order()
            .ToArray();

        var baseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');

        return settings with
        {
            BaseUrl = baseUrl,
            Categories = categories,
            IndexerIds = indexerIds
        };
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

    private sealed record PersistedProwlarrSettings(
        string BaseUrl,
        int[]? Categories,
        int[]? IndexerIds,
        int SearchLimit,
        string ProtectedApiKey);
}
