using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Release;

namespace Jularr.Web.Features.Acquisition.Quality;

// Media-type-agnostic quality-profile store (JSON settings-store pattern under /data). Profiles are
// shared; each registered media type has a default profile and any work can override its profile.
// Anime is one registration (MediaAcquisitionRegistry); Movie/TV/Book register later without
// changing storage. Legacy anime-only files (version 1) are upgraded to the generic shape on load.
public sealed class QualityProfileStore
{
    private const string FileName = "quality-profiles.json";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim gate = new(1, 1);

    // Every search of a Wanted pass resolves its profile: the parsed file is kept until the file changes (its write time and length), so a pass reads it once.
    private (DateTime WrittenAt, long Length, QualityProfileState State)? cached;
    private readonly string storePath;
    private readonly MediaAcquisitionRegistry registry;

    public QualityProfileStore(MediaAcquisitionRegistry registry)
        : this(new DirectoryInfo("/data/acquisition"), registry)
    {
    }

    public QualityProfileStore(DirectoryInfo directory, MediaAcquisitionRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        storePath = Path.Combine(directory.FullName, FileName);
        this.registry = registry ?? new MediaAcquisitionRegistry([new AnimeAcquisitionRegistration()]);
    }

    public async Task<QualityProfileState> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var (state, migrated) = await ReadUnsafeAsync(cancellationToken);
            if (migrated)
            {
                // One-time upgrade of the persisted file into the canonical generic shape.
                await WriteUnsafeAsync(state, cancellationToken);
            }

            return state;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Resolves the quality profile for a media type, honouring a per-work override.</summary>
    public async Task<QualityProfile> ResolveAsync(
        MediaAcquisitionKind kind,
        Guid? workId,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        var profileId = state.ResolveProfileId(kind, workId);
        if (profileId is not null)
        {
            var found = state.Profiles.FirstOrDefault(profile =>
                profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                return found;
            }
        }

        // Kind not configured (or its configured profile is gone): fall back to the registration seed.
        return registry.DefaultProfileFor(kind);
    }

    /// <summary>Back-compat entry point for anime callers.</summary>
    public Task<QualityProfile> ResolveAsync(
        Guid? animeId,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(MediaAcquisitionKind.Anime, animeId, cancellationToken);

    public async Task UpsertAsync(
        QualityProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ThrowIfInvalid(profile);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var (state, _) = await ReadUnsafeAsync(cancellationToken);
            var profiles = state.Profiles.ToList();
            var index = profiles.FindIndex(item =>
                item.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                profiles[index] = profile;
            }
            else
            {
                profiles.Add(profile);
            }

            await WriteUnsafeAsync(state with { Profiles = profiles.ToArray() }, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SetKindDefaultAsync(
        MediaAcquisitionKind kind,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var (state, _) = await ReadUnsafeAsync(cancellationToken);
            EnsureProfileExists(state, profileId);
            var defaults = new Dictionary<string, string>(state.KindDefaults, StringComparer.OrdinalIgnoreCase)
            {
                [AcquisitionAccessNames.Kind(kind)] = profileId
            };
            await WriteUnsafeAsync(state with { KindDefaults = defaults }, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Assigns (or clears, when profileId is null/blank) a per-work profile override.</summary>
    public async Task AssignWorkAsync(
        Guid workId,
        string? profileId,
        CancellationToken cancellationToken = default)
    {
        if (workId == Guid.Empty)
        {
            throw new ArgumentException("Work ID must not be empty.", nameof(workId));
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var (state, _) = await ReadUnsafeAsync(cancellationToken);
            var assignments = new Dictionary<string, string>(
                state.WorkAssignments,
                StringComparer.OrdinalIgnoreCase);
            var key = workId.ToString("D");

            if (string.IsNullOrWhiteSpace(profileId))
            {
                assignments.Remove(key);
            }
            else
            {
                EnsureProfileExists(state, profileId);
                assignments[key] = profileId;
            }

            await WriteUnsafeAsync(
                state with { WorkAssignments = assignments },
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Back-compat entry point for anime callers.</summary>
    public Task AssignAnimeAsync(
        Guid animeId,
        string? profileId,
        CancellationToken cancellationToken = default) =>
        AssignWorkAsync(animeId, profileId, cancellationToken);

    public async Task<bool> DeleteAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var (state, _) = await ReadUnsafeAsync(cancellationToken);
            if (state.KindDefaults.Values.Contains(profileId, StringComparer.OrdinalIgnoreCase) ||
                state.WorkAssignments.Values.Contains(profileId, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            var remaining = state.Profiles
                .Where(profile => !profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (remaining.Length == state.Profiles.Length)
            {
                return false;
            }

            await WriteUnsafeAsync(state with { Profiles = remaining }, cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(QualityProfileState State, bool Migrated)> ReadUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(storePath))
        {
            return (DefaultState(), false);
        }

        var info = new FileInfo(storePath);
        if (cached is { } hit && hit.WrittenAt == info.LastWriteTimeUtc && hit.Length == info.Length)
        {
            return (hit.State, false);
        }

        var json = await File.ReadAllTextAsync(storePath, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(json);
            var version = document.RootElement.TryGetProperty("version", out var versionElement) &&
                          versionElement.TryGetInt32(out var parsedVersion)
                ? parsedVersion
                : 0;

            if (version == 2)
            {
                var upgraded = JsonSerializer.Deserialize<QualityProfileState>(FoldFallbackTiers(json), JsonOptions)
                    ?? throw new InvalidDataException("Quality profile file is empty.");
                var current = upgraded with { Version = QualityProfileState.CurrentVersion };
                ValidateState(current);
                return (current, true);
            }

            if (version < QualityProfileState.CurrentVersion)
            {
                var migrated = MigrateLegacy(document.RootElement);
                ValidateState(migrated);
                return (migrated, true);
            }

            var state = JsonSerializer.Deserialize<QualityProfileState>(json, JsonOptions)
                ?? throw new InvalidDataException("Quality profile file is empty.");

            ValidateState(state);
            cached = (info.LastWriteTimeUtc, info.Length, state);
            return (state, false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Quality profile file contains invalid JSON.",
                exception);
        }
    }

    // Version 2 profiles waited before taking lower qualities; a wait is no part of a profile any more, so those qualities are simply allowed.
    private static string FoldFallbackTiers(string json)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        foreach (var profile in root["profiles"]?.AsArray() ?? [])
        {
            if (profile is not System.Text.Json.Nodes.JsonObject entry)
            {
                continue;
            }

            if (entry["allowedQualities"] is System.Text.Json.Nodes.JsonArray { Count: > 0 } allowed)
            {
                var known = allowed.Select(node => node!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var tier in entry["fallbackTiers"]?.AsArray() ?? [])
                {
                    foreach (var quality in tier?["addedQualities"]?.AsArray() ?? [])
                    {
                        if (known.Add(quality!.GetValue<string>()))
                        {
                            allowed.Add(quality.GetValue<string>());
                        }
                    }
                }
            }

            entry.Remove("fallbackTiers");
        }

        return root.ToJsonString();
    }

    // Version 1 was anime-only: { version, defaultProfileId, profiles, animeProfileAssignments }.
    private static QualityProfileState MigrateLegacy(JsonElement root)
    {
        var profiles = root.TryGetProperty("profiles", out var profilesElement)
            ? JsonSerializer.Deserialize<QualityProfile[]>(profilesElement.GetRawText(), JsonOptions) ?? []
            : [];

        var kindDefaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("defaultProfileId", out var defaultElement) &&
            defaultElement.GetString() is { Length: > 0 } defaultProfileId)
        {
            kindDefaults[AcquisitionAccessNames.Kind(MediaAcquisitionKind.Anime)] = defaultProfileId;
        }

        var assignments = root.TryGetProperty("animeProfileAssignments", out var assignmentsElement)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(assignmentsElement.GetRawText(), JsonOptions) ?? []
            : [];

        return new QualityProfileState(
            QualityProfileState.CurrentVersion,
            profiles,
            kindDefaults,
            new Dictionary<string, string>(assignments, StringComparer.OrdinalIgnoreCase));
    }

    private QualityProfileState DefaultState()
    {
        var profiles = new List<QualityProfile>();
        var kindDefaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kind in registry.Kinds)
        {
            var profile = registry.DefaultProfileFor(kind);
            if (!profiles.Any(existing => existing.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)))
            {
                profiles.Add(profile);
            }

            kindDefaults[AcquisitionAccessNames.Kind(kind)] = profile.Id;
        }

        if (profiles.Count == 0)
        {
            var anime = AnimeQualityProfiles.CreateDefaultAnime1080p();
            profiles.Add(anime);
            kindDefaults[AcquisitionAccessNames.Kind(MediaAcquisitionKind.Anime)] = anime.Id;
        }

        return new QualityProfileState(
            QualityProfileState.CurrentVersion,
            profiles.ToArray(),
            kindDefaults,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    private async Task WriteUnsafeAsync(
        QualityProfileState state,
        CancellationToken cancellationToken)
    {
        ValidateState(state);
        cached = null;

        var directory = Path.GetDirectoryName(storePath)
            ?? throw new InvalidOperationException("Quality profile path has no directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{storePath}.tmp-{Guid.NewGuid():N}";
        try
        {
            var json = JsonSerializer.Serialize(state, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
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

    private static void ValidateState(QualityProfileState state)
    {
        if (state.Version != QualityProfileState.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported quality profile state version {state.Version}.");
        }

        if (state.Profiles is null || state.Profiles.Length == 0)
        {
            throw new InvalidDataException("At least one quality profile is required.");
        }

        if (state.KindDefaults is null)
        {
            throw new InvalidDataException("Quality profile kind defaults are missing.");
        }

        if (state.WorkAssignments is null)
        {
            throw new InvalidDataException("Quality profile work assignments are missing.");
        }

        if (state.Profiles
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("Quality profile IDs must be unique.");
        }

        foreach (var profile in state.Profiles)
        {
            ThrowIfInvalid(profile);
        }

        foreach (var kindDefault in state.KindDefaults)
        {
            EnsureProfileExists(state, kindDefault.Value);
        }

        foreach (var assignment in state.WorkAssignments)
        {
            if (!Guid.TryParse(assignment.Key, out _))
            {
                throw new InvalidDataException(
                    $"Quality profile work assignment key '{assignment.Key}' is not a GUID.");
            }

            EnsureProfileExists(state, assignment.Value);
        }
    }

    private static void ThrowIfInvalid(QualityProfile profile)
    {
        var errors = ReleaseScorer.ValidateProfile(profile);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                $"Quality profile '{profile.Id}' is invalid: {string.Join("; ", errors)}");
        }
    }

    private static void EnsureProfileExists(
        QualityProfileState state,
        string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) ||
            !state.Profiles.Any(profile =>
                profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Quality profile '{profileId}' does not exist.");
        }
    }
}
