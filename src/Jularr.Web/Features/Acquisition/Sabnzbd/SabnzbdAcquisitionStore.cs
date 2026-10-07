using System.Text.Json;
using Jularr.Web.Features.Acquisition.Monitoring;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Web.Features.Acquisition.Sabnzbd;

/// <summary>
/// One accepted release that may be sent to SABnzbd for an acquisition. The indexer and release group travel with it so the download
/// Operation records where the release came from, which is what the selection engine's reliability evidence is built from.
/// </summary>
public sealed record SabnzbdAnimeReleaseCandidate(
    string ReleaseIdentity,
    string ReleaseTitle,
    Uri NzbUrl,
    string? ReleaseSource = null,
    string? ReleaseGroup = null);

/// <summary>
/// One release submitted for an acquisition. Its lifecycle (queued,
/// progress, completed, failed, cancelled) lives only on the referenced
/// canonical Operation.
/// </summary>
public sealed record SabnzbdAcquisitionAttempt(
    int Number,
    Guid OperationId,
    string ReleaseIdentity,
    string ReleaseTitle,
    DateTimeOffset StartedAtUtc);

/// <summary>
/// An accepted release that has not been tried yet. The NZB URL can carry
/// indexer credentials, so it is only persisted in protected form.
/// </summary>
public sealed record SabnzbdPendingCandidate(
    string ReleaseIdentity,
    string ReleaseTitle,
    string ProtectedNzbUrl,
    string? ReleaseSource = null,
    string? ReleaseGroup = null);

/// <summary>
/// Durable relation between an anime acquisition request and the
/// Operations that carry each SABnzbd attempt.
/// </summary>
public sealed record SabnzbdAcquisition(
    Guid Id,
    string AnimeKey,
    string AnimeTitle,
    AnimeEpisodeKey[] Episodes,
    string? ProfileId,
    int MaxAttempts,
    SabnzbdAcquisitionAttempt[] Attempts,
    SabnzbdPendingCandidate[] PendingCandidates,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public SabnzbdAcquisitionAttempt? LatestAttempt =>
        Attempts.Length == 0
            ? null
            : Attempts.MaxBy(attempt => attempt.Number);
}

/// <summary>A release identity that failed and must not be grabbed again.</summary>
public sealed record SabnzbdBlockedRelease(
    string ReleaseIdentity,
    string ReleaseTitle,
    string AnimeKey,
    SabnzbdFailureKind FailureKind,
    string Reason,
    Guid? OperationId,
    DateTimeOffset BlockedAtUtc);

public sealed record SabnzbdAcquisitionStoreState(
    int Version,
    List<SabnzbdAcquisition> Acquisitions,
    List<SabnzbdBlockedRelease> Blocklist)
{
    public static SabnzbdAcquisitionStoreState Empty() =>
        new(1, [], []);

    public bool IsBlocked(string releaseIdentity) =>
        Blocklist.Any(entry =>
            entry.ReleaseIdentity.Equals(
                releaseIdentity,
                StringComparison.OrdinalIgnoreCase));

    public (SabnzbdAcquisition Acquisition, SabnzbdAcquisitionAttempt Attempt)? FindByOperation(
        Guid operationId)
    {
        foreach (var acquisition in Acquisitions)
        {
            var attempt = acquisition.Attempts.FirstOrDefault(
                candidate => candidate.OperationId == operationId);
            if (attempt is not null)
            {
                return (acquisition, attempt);
            }
        }

        return null;
    }
}

public sealed class SabnzbdAcquisitionStore
{
    public const string FileName = "sabnzbd-acquisitions.json";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IDataProtector protector;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storePath;

    public SabnzbdAcquisitionStore(IDataProtectionProvider dataProtectionProvider)
        : this(
            dataProtectionProvider,
            new DirectoryInfo("/data/acquisition"))
    {
    }

    public SabnzbdAcquisitionStore(
        IDataProtectionProvider dataProtectionProvider,
        DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(directory);

        protector = dataProtectionProvider.CreateProtector(
            "Jularr.Acquisition.Sabnzbd.CandidateUrl.v1");
        storePath = Path.Combine(directory.FullName, FileName);
    }

    public async Task<SabnzbdAcquisitionStoreState> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadUnsafeAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SabnzbdAcquisitionStoreState> UpdateAsync(
        Action<SabnzbdAcquisitionStoreState> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadUnsafeAsync(cancellationToken);
            update(state);
            foreach (var acquisition in state.Acquisitions)
            {
                Validate(acquisition);
            }

            await WriteUnsafeAsync(state, cancellationToken);
            return state;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SabnzbdAcquisition?> GetAsync(
        Guid acquisitionId,
        CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).Acquisitions
            .FirstOrDefault(acquisition => acquisition.Id == acquisitionId);

    public async Task<(SabnzbdAcquisition Acquisition, SabnzbdAcquisitionAttempt Attempt)?> FindByOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).FindByOperation(operationId);

    public Task BlockAsync(
        SabnzbdBlockedRelease release,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(release.ReleaseIdentity);

        return UpdateAsync(
            state =>
            {
                if (!state.IsBlocked(release.ReleaseIdentity))
                {
                    state.Blocklist.Add(release);
                }
            },
            cancellationToken);
    }

    public Task UnblockAsync(
        string releaseIdentity,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            state => state.Blocklist.RemoveAll(entry =>
                entry.ReleaseIdentity.Equals(
                    releaseIdentity,
                    StringComparison.OrdinalIgnoreCase)),
            cancellationToken);

    // A series-folder rename changes the library's anime key; acquisitions and blocklist entries
    // follow it so in-flight downloads still import into the same anime.
    public async Task<bool> RekeyAnimeAsync(
        string oldKey,
        string newKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(newKey);
        if (string.Equals(oldKey, newKey, StringComparison.Ordinal))
        {
            return false;
        }

        var current = await LoadAsync(cancellationToken);
        if (!current.Acquisitions.Any(acquisition => IsKey(acquisition.AnimeKey, oldKey)) &&
            !current.Blocklist.Any(entry => IsKey(entry.AnimeKey, oldKey)))
        {
            return false;
        }

        var changed = false;
        await UpdateAsync(
            state =>
            {
                for (var index = 0; index < state.Acquisitions.Count; index++)
                {
                    var acquisition = state.Acquisitions[index];
                    if (!IsKey(acquisition.AnimeKey, oldKey))
                    {
                        continue;
                    }

                    state.Acquisitions[index] = acquisition with
                    {
                        AnimeKey = newKey,
                        Episodes = [.. acquisition.Episodes.Select(episode =>
                            IsKey(episode.AnimeKey, oldKey) ? episode with { AnimeKey = newKey } : episode)]
                    };
                    changed = true;
                }

                for (var index = 0; index < state.Blocklist.Count; index++)
                {
                    if (IsKey(state.Blocklist[index].AnimeKey, oldKey))
                    {
                        state.Blocklist[index] = state.Blocklist[index] with { AnimeKey = newKey };
                        changed = true;
                    }
                }
            },
            cancellationToken);
        return changed;

        static bool IsKey(string? value, string key) =>
            string.Equals(value, key, StringComparison.OrdinalIgnoreCase);
    }

    public string ProtectUrl(Uri nzbUrl)
    {
        ArgumentNullException.ThrowIfNull(nzbUrl);
        return protector.Protect(nzbUrl.AbsoluteUri);
    }

    public Uri UnprotectUrl(string protectedNzbUrl) =>
        new(protector.Unprotect(protectedNzbUrl), UriKind.Absolute);

    private async Task<SabnzbdAcquisitionStoreState> ReadUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(storePath))
        {
            return SabnzbdAcquisitionStoreState.Empty();
        }

        try
        {
            var json = await File.ReadAllTextAsync(storePath, cancellationToken);
            var state = JsonSerializer.Deserialize<SabnzbdAcquisitionStoreState>(
                    json,
                    JsonOptions)
                ?? SabnzbdAcquisitionStoreState.Empty();

            return state with
            {
                Acquisitions = state.Acquisitions ?? [],
                Blocklist = state.Blocklist ?? []
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "SABnzbd acquisition state contains invalid JSON.",
                exception);
        }
    }

    private async Task WriteUnsafeAsync(
        SabnzbdAcquisitionStoreState state,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(storePath)
            ?? throw new InvalidOperationException(
                "SABnzbd acquisition path has no directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{storePath}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(state, JsonOptions),
                cancellationToken);
            File.Move(temporaryPath, storePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void Validate(SabnzbdAcquisition acquisition)
    {
        if (acquisition.Id == Guid.Empty
            || string.IsNullOrWhiteSpace(acquisition.AnimeKey)
            || string.IsNullOrWhiteSpace(acquisition.AnimeTitle)
            || acquisition.Episodes is null
            || acquisition.MaxAttempts < 1
            || acquisition.Attempts is null
            || acquisition.PendingCandidates is null
            || acquisition.Attempts.Any(attempt =>
                attempt.OperationId == Guid.Empty
                || string.IsNullOrWhiteSpace(attempt.ReleaseIdentity))
            || acquisition.PendingCandidates.Any(candidate =>
                string.IsNullOrWhiteSpace(candidate.ReleaseIdentity)
                || string.IsNullOrWhiteSpace(candidate.ProtectedNzbUrl)))
        {
            throw new InvalidDataException(
                "SABnzbd acquisition contains invalid required fields.");
        }
    }
}
