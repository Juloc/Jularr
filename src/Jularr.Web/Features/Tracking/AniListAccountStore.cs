using System.Security.Cryptography;
using System.Text.Json;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Metadata;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Web.Features.Tracking;

public sealed record StoredAniListAccount(
    int ClientId,
    int ViewerId,
    string ViewerName,
    string? ViewerAvatarUrl,
    string AccessToken,
    DateTimeOffset ConnectedAt,
    DateTimeOffset? TokenExpiresAt,
    AniListSyncMode SyncMode = AniListSyncMode.Off,
    DateTimeOffset? SyncEnabledAt = null);

public sealed class AniListAccountStore
{
    private const string LegacyAccountFileName = "anilist.json";
    private const string ProgressBackupFileName = "anilist-progress-backups.ndjson";
    private const string EpisodeMappingsFileName = "anilist-episode-mappings.json";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IDataProtector protector;
    private readonly ILogger<AniListAccountStore> logger;
    private readonly string integrationDirectory;
    private readonly string accountDirectory;
    private readonly string legacyAccountPath;
    private readonly string progressBackupPath;
    private readonly string episodeMappingsPath;

    private readonly SemaphoreSlim accountGate = new(1, 1);
    private readonly SemaphoreSlim backupGate = new(1, 1);
    private readonly SemaphoreSlim episodeMappingsGate = new(1, 1);

    public AniListAccountStore(
        IDataProtectionProvider dataProtectionProvider,
        ILogger<AniListAccountStore> logger)
        : this(
            dataProtectionProvider,
            logger,
            new DirectoryInfo("/data/integrations"))
    {
    }

    public AniListAccountStore(
        IDataProtectionProvider dataProtectionProvider,
        ILogger<AniListAccountStore> logger,
        DirectoryInfo integrationDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(integrationDirectory);

        protector = dataProtectionProvider.CreateProtector(
            "Jularr.AniList.AccessToken.v1");
        this.logger = logger;
        this.integrationDirectory = integrationDirectory.FullName;
        accountDirectory = Path.Combine(
            this.integrationDirectory,
            "anilist",
            "accounts");
        legacyAccountPath = Path.Combine(
            this.integrationDirectory,
            LegacyAccountFileName);
        progressBackupPath = Path.Combine(
            this.integrationDirectory,
            ProgressBackupFileName);
        episodeMappingsPath = Path.Combine(
            this.integrationDirectory,
            EpisodeMappingsFileName);
    }

    public async Task<StoredAniListAccount?> LoadAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        var safeProfileId = ValidateProfileId(profileId);

        await accountGate.WaitAsync(cancellationToken);
        try
        {
            MigrateLegacyOwnerAccountUnsafe(safeProfileId);
            return await ReadAccountUnsafeAsync(
                GetAccountPath(safeProfileId),
                cancellationToken);
        }
        finally
        {
            accountGate.Release();
        }
    }

    public async Task SaveAsync(
        string profileId,
        StoredAniListAccount account,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        var safeProfileId = ValidateProfileId(profileId);

        await accountGate.WaitAsync(cancellationToken);
        try
        {
            MigrateLegacyOwnerAccountUnsafe(safeProfileId);

            Directory.CreateDirectory(accountDirectory);
            var storePath = GetAccountPath(safeProfileId);
            var persisted = new PersistedAniListAccount(
                account.ClientId,
                account.ViewerId,
                account.ViewerName,
                account.ViewerAvatarUrl,
                protector.Protect(account.AccessToken),
                account.ConnectedAt,
                account.TokenExpiresAt,
                account.SyncMode,
                account.SyncEnabledAt);

            await WriteAccountUnsafeAsync(storePath, persisted, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                exception,
                "Could not save the AniList account connection for profile {ProfileId}.",
                safeProfileId);
            throw new AniListAccountException(
                "Your AniList connection could not be saved to persistent storage.",
                exception);
        }
        finally
        {
            accountGate.Release();
        }
    }

    /// <summary>
    /// Stores the profile's automatic AniList sync mode next to its connection.
    /// Turning sync on records when it was enabled: automatic sync only
    /// considers local progress made after that moment. Returns false when the
    /// profile has no AniList connection.
    /// </summary>
    public async Task<bool> UpdateSyncModeAsync(
        string profileId,
        AniListSyncMode mode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var safeProfileId = ValidateProfileId(profileId);

        await accountGate.WaitAsync(cancellationToken);
        try
        {
            MigrateLegacyOwnerAccountUnsafe(safeProfileId);
            var storePath = GetAccountPath(safeProfileId);
            if (!File.Exists(storePath))
            {
                return false;
            }

            var persisted = JsonSerializer.Deserialize<PersistedAniListAccount>(
                await File.ReadAllTextAsync(storePath, cancellationToken),
                JsonOptions);
            if (persisted is null)
            {
                return false;
            }

            var enabledAt = mode == AniListSyncMode.Off
                ? null
                : persisted.SyncMode == AniListSyncMode.Off || persisted.SyncEnabledAt is null
                    ? now
                    : persisted.SyncEnabledAt;

            await WriteAccountUnsafeAsync(
                storePath,
                persisted with { SyncMode = mode, SyncEnabledAt = enabledAt },
                cancellationToken);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogError(
                exception,
                "Could not save the AniList sync mode for profile {ProfileId}.",
                safeProfileId);
            throw new AniListAccountException(
                "The AniList sync setting could not be saved to persistent storage.",
                exception);
        }
        finally
        {
            accountGate.Release();
        }
    }

    public async Task DisconnectAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var safeProfileId = ValidateProfileId(profileId);

        await accountGate.WaitAsync(cancellationToken);
        try
        {
            MigrateLegacyOwnerAccountUnsafe(safeProfileId);
            TryDelete(GetAccountPath(safeProfileId));
        }
        finally
        {
            accountGate.Release();
        }
    }

    public async Task AppendProgressBackupAsync(
        AniListProgressBackup backup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ValidateProfileId(backup.ProfileId);

        var directory = Path.GetDirectoryName(progressBackupPath)
            ?? throw new InvalidOperationException(
                "AniList backup path has no directory.");

        Directory.CreateDirectory(directory);

        await backupGate.WaitAsync(cancellationToken);
        try
        {
            var line = JsonSerializer.Serialize(
                backup,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            await File.AppendAllTextAsync(
                progressBackupPath,
                line + Environment.NewLine,
                cancellationToken);

            SetPrivateFileMode(progressBackupPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                exception,
                "Could not persist AniList pre-write progress backup.");

            // Never write to AniList if the safety backup cannot be persisted.
            throw new AniListAccountException(
                "AniList sync was not attempted because the local pre-write backup could not be saved.",
                exception);
        }
        finally
        {
            backupGate.Release();
        }
    }

    public async Task<IReadOnlyList<AnimeEpisodeMetadataMapping>> LoadEpisodeMappingsAsync(
        Guid animeId,
        CancellationToken cancellationToken)
    {
        await episodeMappingsGate.WaitAsync(cancellationToken);
        try
        {
            var mappings = await ReadEpisodeMappingsUnsafeAsync(cancellationToken);
            return mappings
                .Where(x => x.AnimeId == animeId)
                .OrderBy(x => x.SeasonNumber)
                .ThenBy(x => x.LocalEpisodeStart)
                .ToArray();
        }
        finally
        {
            episodeMappingsGate.Release();
        }
    }

    public async Task<IReadOnlyList<AnimeEpisodeMetadataMapping>> LoadAllEpisodeMappingsAsync(
        CancellationToken cancellationToken)
    {
        await episodeMappingsGate.WaitAsync(cancellationToken);
        try
        {
            return (await ReadEpisodeMappingsUnsafeAsync(cancellationToken)).ToArray();
        }
        finally
        {
            episodeMappingsGate.Release();
        }
    }

    public async Task<bool> TryAddEpisodeMappingAsync(
        AnimeEpisodeMetadataMapping mapping,
        CancellationToken cancellationToken)
    {
        await episodeMappingsGate.WaitAsync(cancellationToken);
        try
        {
            var mappings = await ReadEpisodeMappingsUnsafeAsync(cancellationToken);

            if (mappings.Any(x =>
                    x.AnimeId == mapping.AnimeId &&
                    AnimeEpisodeMetadataRules.Overlaps(
                        x,
                        mapping.SeasonNumber,
                        mapping.LocalEpisodeStart,
                        mapping.LocalEpisodeEnd)))
            {
                return false;
            }

            mappings.Add(mapping);
            await WriteEpisodeMappingsUnsafeAsync(mappings, cancellationToken);
            return true;
        }
        finally
        {
            episodeMappingsGate.Release();
        }
    }

    public async Task<bool> RemoveEpisodeMappingAsync(
        Guid animeId,
        Guid mappingId,
        CancellationToken cancellationToken)
    {
        await episodeMappingsGate.WaitAsync(cancellationToken);
        try
        {
            var mappings = await ReadEpisodeMappingsUnsafeAsync(cancellationToken);
            var removed = mappings.RemoveAll(
                x => x.AnimeId == animeId && x.Id == mappingId) > 0;

            if (removed)
            {
                await WriteEpisodeMappingsUnsafeAsync(mappings, cancellationToken);
            }

            return removed;
        }
        finally
        {
            episodeMappingsGate.Release();
        }
    }

    private async Task<StoredAniListAccount?> ReadAccountUnsafeAsync(
        string storePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(storePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(storePath, cancellationToken);
            var persisted = JsonSerializer.Deserialize<PersistedAniListAccount>(
                json,
                JsonOptions);

            if (persisted is null ||
                persisted.ClientId <= 0 ||
                persisted.ViewerId <= 0 ||
                string.IsNullOrWhiteSpace(persisted.ViewerName) ||
                string.IsNullOrWhiteSpace(persisted.ProtectedAccessToken))
            {
                return null;
            }

            return new StoredAniListAccount(
                persisted.ClientId,
                persisted.ViewerId,
                persisted.ViewerName,
                persisted.ViewerAvatarUrl,
                protector.Unprotect(persisted.ProtectedAccessToken),
                persisted.ConnectedAt,
                persisted.TokenExpiresAt,
                persisted.SyncMode,
                persisted.SyncEnabledAt);
        }
        catch (Exception exception) when (
            exception is JsonException or
            CryptographicException or
            IOException or
            UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "Could not load an AniList account connection from {Path}.",
                storePath);
            return null;
        }
    }

    private void MigrateLegacyOwnerAccountUnsafe(string profileId)
    {
        if (!string.Equals(
                profileId,
                OwnerAccount.SingletonId,
                StringComparison.Ordinal) ||
            !File.Exists(legacyAccountPath))
        {
            return;
        }

        Directory.CreateDirectory(accountDirectory);
        var ownerPath = GetAccountPath(OwnerAccount.SingletonId);

        if (File.Exists(ownerPath))
        {
            TryDelete(legacyAccountPath);
            return;
        }

        try
        {
            File.Move(legacyAccountPath, ownerPath);
            SetPrivateFileMode(ownerPath);
            logger.LogInformation(
                "Migrated the legacy server-wide AniList connection to the Owner profile.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                exception,
                "Could not migrate the legacy AniList connection to the Owner profile.");
            throw new AniListAccountException(
                "The legacy AniList connection could not be migrated to the Owner account.",
                exception);
        }
    }

    private async Task WriteAccountUnsafeAsync(
        string storePath,
        PersistedAniListAccount persisted,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(accountDirectory);
        var temporaryPath = $"{storePath}.tmp-{Guid.NewGuid():N}";
        var json = JsonSerializer.Serialize(persisted, JsonOptions);

        try
        {
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

    private string GetAccountPath(string profileId) =>
        Path.Combine(accountDirectory, $"{profileId}.json");

    internal static string ValidateProfileId(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        if (profileId.Length > 80 ||
            profileId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_'))
        {
            throw new ArgumentException(
                "Profile ID contains unsupported characters.",
                nameof(profileId));
        }

        return profileId;
    }

    private async Task<List<AnimeEpisodeMetadataMapping>> ReadEpisodeMappingsUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(episodeMappingsPath))
        {
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(
                episodeMappingsPath,
                cancellationToken);

            return JsonSerializer.Deserialize<List<AnimeEpisodeMetadataMapping>>(
                    json,
                    JsonOptions)
                ?? [];
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                exception,
                "Could not load AniList episode mappings from {Path}.",
                episodeMappingsPath);
            throw new AniListAccountException(
                "AniList episode mappings could not be read from persistent storage.",
                exception);
        }
    }

    private async Task WriteEpisodeMappingsUnsafeAsync(
        IReadOnlyCollection<AnimeEpisodeMetadataMapping> mappings,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(episodeMappingsPath)
            ?? throw new InvalidOperationException(
                "AniList episode mapping path has no directory.");

        Directory.CreateDirectory(directory);

        var temporaryPath = $"{episodeMappingsPath}.tmp-{Guid.NewGuid():N}";
        var json = JsonSerializer.Serialize(mappings, JsonOptions);

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                cancellationToken);

            SetPrivateFileMode(temporaryPath);
            File.Move(temporaryPath, episodeMappingsPath, overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                exception,
                "Could not save AniList episode mappings to {Path}.",
                episodeMappingsPath);
            throw new AniListAccountException(
                "AniList episode mappings could not be saved to persistent storage.",
                exception);
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

    private sealed record PersistedAniListAccount(
        int ClientId,
        int ViewerId,
        string ViewerName,
        string? ViewerAvatarUrl,
        string ProtectedAccessToken,
        DateTimeOffset ConnectedAt,
        DateTimeOffset? TokenExpiresAt,
        AniListSyncMode SyncMode = AniListSyncMode.Off,
        DateTimeOffset? SyncEnabledAt = null);
}
