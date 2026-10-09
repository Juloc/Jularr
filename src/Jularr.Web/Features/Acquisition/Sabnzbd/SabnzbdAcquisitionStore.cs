using System.Text.Json;
using Jularr.Web.Features.Acquisition.Monitoring;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Web.Features.Acquisition.Sabnzbd;

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

/// <summary>An accepted release the old pipeline had not tried yet; its URL stays in protected form and is not read any more.</summary>
public sealed record SabnzbdPendingCandidate(
    string ReleaseIdentity,
    string ReleaseTitle,
    string ProtectedNzbUrl,
    string? ReleaseSource = null,
    string? ReleaseGroup = null);

/// <summary>An acquisition of the old Anime pipeline; only <see cref="Pipeline.AnimeLegacyAcquisitionMigration"/> reads it, to move unfinished downloads onto requests.</summary>
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
}

public sealed class SabnzbdAcquisitionStore
{
    public const string FileName = "sabnzbd-acquisitions.json";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storePath;

    public SabnzbdAcquisitionStore()
        : this(new DirectoryInfo("/data/acquisition"))
    {
    }

    public SabnzbdAcquisitionStore(DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

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
            await WriteUnsafeAsync(state, cancellationToken);
            return state;
        }
        finally
        {
            gate.Release();
        }
    }

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

    // A series-folder rename changes the library's anime key; the blocklist entries follow it.
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
        if (!current.Blocklist.Any(entry => IsKey(entry.AnimeKey, oldKey)))
        {
            return false;
        }

        var changed = false;
        await UpdateAsync(
            state =>
            {
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
}
