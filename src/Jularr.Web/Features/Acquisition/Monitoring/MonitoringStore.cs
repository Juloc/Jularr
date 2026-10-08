using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jularr.Web.Features.Acquisition.Monitoring;

/// <summary>
/// The JSON settings store of the anime monitoring state (<c>/data/acquisition/monitoring.json</c>).
/// State is a single-writer JSON document written atomically via a temp file + move, guarded by an
/// in-process gate. Movie and TV monitoring is not stored here: it is part of the request payload
/// (VideoMonitoringService).
/// </summary>
public sealed class MonitoringStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public MonitoringStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _path = Path.Combine(dataRoot, "acquisition", "monitoring.json");
    }

    public async Task<MonitoringState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadUnlockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        MonitoringState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(state, JsonOptions),
                cancellationToken);

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MonitoringState> UpdateAsync(
        Func<MonitoringState, MonitoringState> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadUnlockedAsync(cancellationToken);
            var updated = update(state);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(updated, JsonOptions),
                cancellationToken);
            File.Move(temporary, _path, overwrite: true);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RekeyAnimeAsync(
        string oldKey,
        string newKey,
        CancellationToken cancellationToken = default)
    {
        var current = await LoadAsync(cancellationToken);
        if (ReferenceEquals(MonitoringEngine.RekeyAnime(current, oldKey, newKey), current))
        {
            return false;
        }

        await UpdateAsync(state => MonitoringEngine.RekeyAnime(state, oldKey, newKey), cancellationToken);
        return true;
    }

    /// <summary>What an older version kept in this file about whether an anime and its seasons and episodes are monitored.</summary>
    public sealed record LegacyDecisions(string AnimeKey, bool Monitored, IReadOnlyDictionary<int, bool> Seasons, IReadOnlyDictionary<(int Season, int Episode), bool> Episodes);

    /// <summary>The decisions an older version kept in this file; empty once <see cref="RemoveLegacyDecisionsAsync"/> ran.</summary>
    public async Task<IReadOnlyList<LegacyDecisions>> ReadLegacyDecisionsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await ReadNodeUnlockedAsync(cancellationToken) is not { } root || root["anime"] is not JsonObject anime)
            {
                return [];
            }

            var found = new List<LegacyDecisions>();
            foreach (var (key, node) in anime)
            {
                if (node is not JsonObject settings || settings["monitored"] is null)
                {
                    continue;
                }

                var seasons = (settings["seasonOverrides"] as JsonObject ?? []).ToDictionary(pair => int.Parse(pair.Key), pair => pair.Value!.GetValue<bool>());
                var episodes = new Dictionary<(int, int), bool>();
                foreach (var (name, value) in settings["episodeOverrides"] as JsonObject ?? [])
                {
                    if (Regex.Match(name, "^S(\\d+)E(\\d+)$", RegexOptions.IgnoreCase) is { Success: true } match)
                    {
                        episodes[(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value))] = value!.GetValue<bool>();
                    }
                }

                found.Add(new LegacyDecisions(settings["animeKey"]?.GetValue<string>() ?? key, settings["monitored"]!.GetValue<bool>(), seasons, episodes));
            }

            return found;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Takes the legacy decisions out of the file once they live in the canonical Monitoring state.</summary>
    public async Task RemoveLegacyDecisionsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await ReadNodeUnlockedAsync(cancellationToken) is not { } root || root["anime"] is not JsonObject anime)
            {
                return;
            }

            foreach (var (_, node) in anime)
            {
                if (node is JsonObject settings)
                {
                    settings.Remove("monitored");
                    settings.Remove("seasonOverrides");
                    settings.Remove("episodeOverrides");
                }
            }

            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, root.ToJsonString(JsonOptions), cancellationToken);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonNode?> ReadNodeUnlockedAsync(CancellationToken cancellationToken) =>
        File.Exists(_path) ? JsonNode.Parse(await File.ReadAllTextAsync(_path, cancellationToken)) : null;

    private async Task<MonitoringState> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return MonitoringState.Empty();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_path, cancellationToken);
            var state = JsonSerializer.Deserialize<MonitoringState>(json, JsonOptions);
            return state ?? MonitoringState.Empty();
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"Monitoring state '{_path}' is invalid JSON.");
        }
    }
}
