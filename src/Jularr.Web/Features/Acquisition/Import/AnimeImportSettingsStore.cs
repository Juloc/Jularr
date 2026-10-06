using Jularr.Web.Features.Acquisition.Access;
using System.Text.Json;

namespace Jularr.Web.Features.Acquisition.Import;

/// <summary>
/// Canonical store for <see cref="AnimeImportSettingsState"/> (import mode per root/global and
/// remote path mappings), following the same atomic read-modify-write convention as the other
/// acquisition JSON stores.
/// </summary>
public sealed class AnimeImportSettingsStore
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public AnimeImportSettingsStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        path = Path.Combine(dataRoot, "acquisition", "import-settings.json");
    }

    public async Task<AnimeImportSettingsState> LoadAsync(CancellationToken cancellationToken = default)
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

    public async Task<AnimeImportSettingsState> UpdateAsync(
        Func<AnimeImportSettingsState, AnimeImportSettingsState> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadUnlockedAsync(cancellationToken);
            var updated = update(current);
            if (!ReferenceEquals(updated, current))
            {
                await SaveUnlockedAsync(updated, cancellationToken);
            }

            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AnimeImportSettingsState> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return AnimeImportSettingsState.Empty();
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var state = JsonSerializer.Deserialize<AnimeImportSettingsState>(json, JsonOptions)
                        ?? AnimeImportSettingsState.Empty();
            state = state with
            {
                RootImportModes = new Dictionary<Guid, ImportMode>(state.RootImportModes ?? []),
                MediaLibraries = new Dictionary<MediaAcquisitionKind, MediaLibraryTarget>(state.MediaLibraries ?? [])
            };

            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Import settings '{path}' is invalid JSON.", exception);
        }
    }

    private async Task SaveUnlockedAsync(AnimeImportSettingsState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, JsonOptions), cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }
}
