using System.Text.Json;
using Jularr.Web.Features.Providers;

namespace Jularr.Web.Features.ExternalPlayback.Plex;

public sealed record PlexCatalogCheckpoint(
    string MachineIdentifier,
    string SectionId,
    Guid Generation,
    DateTimeOffset GrantUpdatedAtUtc,
    int PageSize,
    int? NextStart,
    int TotalSize,
    int PagesScanned,
    DateTimeOffset UpdatedAtUtc)
{
    public bool Complete => NextStart is null;
}

public sealed record PlexCatalogMappedItem(string RatingKey, long WorkId);

/// <summary>
/// Durable nonsecret checkpoints and page snapshots for the *existing* Plex
/// catalog scan pipeline. Each completed source page is atomic and restartable.
/// A scan never copies a Plex access token, playback URL or watched progress.
/// </summary>
public sealed class PlexCatalogCheckpointStore(
    TimeProvider clock,
    string directory = "/data/integrations/plex/catalog")
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<PlexCatalogCheckpoint?> GetAsync(
        string machineId,
        string sectionId,
        CancellationToken cancellationToken = default)
    {
        var path = CheckpointPath(machineId, sectionId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadAsync(path, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PlexCatalogCheckpoint> BeginAsync(
        string machineId,
        string sectionId,
        DateTimeOffset grantUpdatedAtUtc,
        int pageSize,
        bool forceRestart,
        CancellationToken cancellationToken = default)
    {
        var path = CheckpointPath(machineId, sectionId);
        if (pageSize is < 1 or > PlexLibraryClient.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var previous = await ReadAsync(path, cancellationToken);
            if (!forceRestart && previous is not null &&
                previous.GrantUpdatedAtUtc == grantUpdatedAtUtc &&
                previous.PageSize == pageSize)
            {
                return previous;
            }

            var started = new PlexCatalogCheckpoint(
                machineId, sectionId, Guid.NewGuid(), grantUpdatedAtUtc,
                pageSize, 0, 0, 0, clock.GetUtcNow());

            await ProviderCredentialFile.WriteAtomicAsync(
                path,
                JsonSerializer.Serialize(started, JsonOptions),
                cancellationToken);

            // Do not accumulate stale snapshots after an administrator changes
            // the server/library grant or deliberately restarts a scan.
            if (previous is not null)
            {
                var oldPages = Path.Combine(
                    Path.GetDirectoryName(path)!,
                    previous.Generation.ToString("N"));
                if (Directory.Exists(oldPages))
                {
                    try
                    {
                        Directory.Delete(oldPages, recursive: true);
                    }
                    catch (IOException)
                    {
                        // Cleanup is optional; new generation is already durable.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Permissions are reported by the later periodic cleanup.
                    }
                }
            }

            return started;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PlexCatalogCheckpoint> CommitAsync(
        PlexCatalogCheckpoint expected,
        PlexCatalogScanPage page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(page);
        var path = CheckpointPath(expected.MachineIdentifier, expected.SectionId);
        if (page.MachineIdentifier != expected.MachineIdentifier ||
            page.SectionId != expected.SectionId || page.Start != expected.NextStart ||
            expected.Complete || page.Matches.Count > PlexLibraryClient.MaxPageSize ||
            page.TotalSize < 0 || page.NextStart is < 0 ||
            page.NextStart is { } next && next <= page.Start)
        {
            throw new InvalidOperationException("Unexpected Plex scan page cursor.");
        }

        var matches = page.Matches
            .Where(x => x.WorkId is > 0 &&
                x.Item.RatingKey.Length is > 0 and <= 18 &&
                x.Item.RatingKey.All(char.IsAsciiDigit))
            .Select(x => new PlexCatalogMappedItem(
                x.Item.RatingKey, x.WorkId!.Value))
            .Distinct()
            .ToArray();

        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadAsync(path, cancellationToken);
            if (current is null ||
                current.Generation != expected.Generation ||
                current.NextStart != expected.NextStart ||
                current.GrantUpdatedAtUtc != expected.GrantUpdatedAtUtc)
            {
                throw new InvalidOperationException(
                    "The Plex scan checkpoint has changed during scanning.");
            }

            // Write the immutable page first; interrupted commits are retried
            // idempotently from the previous cursor on the next run.
            var dataPath = Path.Combine(
                Path.GetDirectoryName(path)!,
                current.Generation.ToString("N"),
                $"page-{page.Start:D10}.json");
            await ProviderCredentialFile.WriteAtomicAsync(
                dataPath,
                JsonSerializer.Serialize(matches, JsonOptions),
                cancellationToken);

            var advanced = current with
            {
                NextStart = page.NextStart,
                TotalSize = page.TotalSize,
                PagesScanned = checked(current.PagesScanned + 1),
                UpdatedAtUtc = clock.GetUtcNow()
            };
            await ProviderCredentialFile.WriteAtomicAsync(
                path,
                JsonSerializer.Serialize(advanced, JsonOptions),
                cancellationToken);
            return advanced;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<PlexCatalogMappedItem>> ReadPageAsync(
        PlexCatalogCheckpoint checkpoint,
        int start,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (start < 0 || start > 1_000_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        var path = CheckpointPath(
            checkpoint.MachineIdentifier, checkpoint.SectionId);
        var pagePath = Path.Combine(
            Path.GetDirectoryName(path)!,
            checkpoint.Generation.ToString("N"),
            $"page-{start:D10}.json");

        await gate.WaitAsync(cancellationToken);
        try
        {
            var latest = await ReadAsync(path, cancellationToken);
            if (latest?.Generation != checkpoint.Generation ||
                latest.NextStart is { } next && start >= next ||
                !File.Exists(pagePath))
            {
                // Pages written before a failed checkpoint commit are not
                // visible until that cursor has actually advanced.
                return [];
            }

            var data = await File.ReadAllTextAsync(
                pagePath, cancellationToken);
            return JsonSerializer.Deserialize<PlexCatalogMappedItem[]>(
                data, JsonOptions) ?? [];
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<PlexCatalogCheckpoint?> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        var checkpoint = JsonSerializer.Deserialize<PlexCatalogCheckpoint>(
            content, JsonOptions);
        if (checkpoint is null || checkpoint.Generation == Guid.Empty ||
            checkpoint.PageSize is < 1 or > PlexLibraryClient.MaxPageSize ||
            checkpoint.PagesScanned < 0 || checkpoint.TotalSize < 0 ||
            checkpoint.NextStart is < 0)
        {
            throw new InvalidDataException("Invalid Plex scan checkpoint.");
        }

        return checkpoint;
    }

    private string CheckpointPath(string machineId, string sectionId)
    {
        if (string.IsNullOrWhiteSpace(machineId) ||
            machineId.Length is < 8 or > 160 ||
            !machineId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ||
            string.IsNullOrWhiteSpace(sectionId) ||
            sectionId.Length > 12 ||
            !sectionId.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("Invalid Plex scan scope.");
        }

        return Path.Combine(directory, machineId, sectionId, "checkpoint.json");
    }
}
