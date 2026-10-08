using System.Text.Json;

namespace Jularr.Web.Features.Acquisition.Backup;

/// <summary>
/// Exports/imports one JSON bundle of every canonical acquisition JSON store (P1 item 8), with a
/// dry-run preview before anything is written. This only ever touches the acquisition stores'
/// files directly (as raw JSON text) so a restore can never drift from what each store itself would
/// produce; it never parses store-specific models, so it stays correct as those models evolve.
/// </summary>
public sealed class AcquisitionBackupService
{
    public const int CurrentVersion = 1;

    // Every canonical acquisition JSON store, relative to /data/acquisition. Keep this in sync
    // when a new canonical acquisition store is added. health.json (AcquisitionHealthStore) is
    // deliberately excluded: it is runtime health state (reachable/last error/last check), not a
    // setting, and restoring it on a different installation would be meaningless.
    private static readonly IReadOnlyList<string> KnownFiles =
    [
        "monitoring.json",
        "quality-profiles.json",
        "indexers.json",
        "download-clients.json",
        "sabnzbd-acquisitions.json",
        "imports.json",
        "ownership.json",
        "naming-profiles.json",
        "import-settings.json",
        "anilist-auto-monitor.json"
    ];

    private static readonly IReadOnlySet<string> SecretFiles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "indexers.json", "download-clients.json" };

    private readonly string directory;

    public AcquisitionBackupService(string dataRoot = "/data")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        directory = Path.Combine(dataRoot, "acquisition");
    }

    public async Task<AcquisitionBackupBundle> ExportAsync(CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in KnownFiles)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                files[fileName] = await File.ReadAllTextAsync(path, cancellationToken);
            }
        }

        return new AcquisitionBackupBundle(CurrentVersion, DateTimeOffset.UtcNow, files);
    }

    /// <summary>Validates the bundle and reports what would change, without writing anything.</summary>
    public async Task<AcquisitionBackupPreview> PreviewRestoreAsync(
        AcquisitionBackupBundle bundle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var errors = new List<string>();
        if (bundle.Version != CurrentVersion)
        {
            errors.Add($"Backup version {bundle.Version} is not supported (expected {CurrentVersion}).");
        }

        var changes = new List<AcquisitionBackupFileChange>();
        foreach (var (fileName, content) in bundle.Files)
        {
            if (!KnownFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"'{fileName}' is not a recognized acquisition settings file.");
                continue;
            }

            var isValidJson = IsValidJson(content);
            if (!isValidJson)
            {
                errors.Add($"'{fileName}' does not contain valid JSON.");
            }

            var path = Path.Combine(directory, fileName);
            var existed = File.Exists(path);
            var unchanged = existed && await File.ReadAllTextAsync(path, cancellationToken) == content;
            changes.Add(new AcquisitionBackupFileChange(fileName, existed, SecretFiles.Contains(fileName), isValidJson, WouldChange: !unchanged));
        }

        return new AcquisitionBackupPreview(errors.Count == 0, changes, errors);
    }

    /// <summary>Restores every file in the bundle atomically. Refuses entirely when validation fails.</summary>
    public async Task<AcquisitionBackupRestoreResult> RestoreAsync(
        AcquisitionBackupBundle bundle,
        CancellationToken cancellationToken)
    {
        var preview = await PreviewRestoreAsync(bundle, cancellationToken);
        if (!preview.CanRestore)
        {
            return new(false, 0, preview.Errors);
        }

        Directory.CreateDirectory(directory);
        var written = 0;
        foreach (var (fileName, content) in bundle.Files)
        {
            var path = Path.Combine(directory, fileName);
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, content, cancellationToken);
            File.Move(temporary, path, overwrite: true);
            written++;
        }

        return new(true, written, []);
    }

    private static bool IsValidJson(string content)
    {
        try
        {
            using var _ = JsonDocument.Parse(content);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
