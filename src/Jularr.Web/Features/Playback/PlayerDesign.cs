using System.Globalization;
using System.Text.Json;
using Jularr.Web.Features.Progress;

namespace Jularr.Web.Features.Playback;

public sealed record PlayerIcon(string ViewBox, string Path);

/// <summary>
/// Read-only view of the canonical player design inputs in
/// <c>design/player/*.json</c>, embedded into the web assembly at build time.
/// The JSON files stay the single source for web and Android; this class only
/// exposes the values the server renders or validates against.
/// </summary>
public static class PlayerDesign
{
    private const string TokensResource = "Jularr.Design.Player.player-tokens.json";
    private const string IconsResource = "Jularr.Design.Player.player-icons.json";

    private static readonly Lazy<IReadOnlyDictionary<string, PlayerIcon>> LazyIcons = new(LoadIcons);
    private static readonly Lazy<IReadOnlyList<double>> LazySpeeds = new(LoadSpeeds);
    private static readonly Lazy<int> LazySeekStep = new(() =>
        ReadTokens(root => root.GetProperty("playback").GetProperty("seekStepSeconds").GetInt32()));

    public static IReadOnlyDictionary<string, PlayerIcon> Icons => LazyIcons.Value;

    /// <summary>Allowed playback speeds, ascending; always contains 1.0.</summary>
    public static IReadOnlyList<double> PlaybackSpeeds => LazySpeeds.Value;

    public static int SeekStepSeconds => LazySeekStep.Value;

    /// <summary>The icons the player scripts swap at runtime (play/pause, mute, full screen); every other icon is rendered by the server.</summary>
    public static IReadOnlyDictionary<string, PlayerIcon> ScriptIcons { get; } = new[] { "play", "pause", "volume", "volumeMuted", "fullscreen", "fullscreenExit" }.ToDictionary(id => id, Icon);

    public static PlayerIcon Icon(string id) =>
        Icons.TryGetValue(id, out var icon)
            ? icon
            : throw new KeyNotFoundException($"Player icon '{id}' is not defined in design/player/player-icons.json.");

    public static string FormatSpeed(double speed) =>
        $"{speed.ToString("0.##", CultureInfo.InvariantCulture)}×";

    private static IReadOnlyDictionary<string, PlayerIcon> LoadIcons()
    {
        using var document = Open(IconsResource);
        return document.RootElement
            .GetProperty("icons")
            .EnumerateObject()
            .ToDictionary(
                x => x.Name,
                x => new PlayerIcon(
                    x.Value.GetProperty("viewBox").GetString()!,
                    x.Value.GetProperty("path").GetString()!),
                StringComparer.Ordinal);
    }

    private static IReadOnlyList<double> LoadSpeeds()
    {
        var speeds = ReadTokens(root => root
            .GetProperty("playback")
            .GetProperty("speeds")
            .EnumerateArray()
            .Select(x => x.GetDouble())
            .ToArray());

        if (speeds.Length == 0 ||
            !speeds.Contains(PlaybackPreferenceRules.DefaultSpeed) ||
            !speeds.SequenceEqual(speeds.Order()))
        {
            throw new InvalidOperationException(
                "design/player/player-tokens.json playback.speeds must be ascending and contain 1.0.");
        }

        return speeds;
    }

    private static T ReadTokens<T>(Func<JsonElement, T> read)
    {
        using var document = Open(TokensResource);
        return read(document.RootElement);
    }

    private static JsonDocument Open(string resource)
    {
        using var stream = typeof(PlayerDesign).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded player design resource '{resource}' is missing.");
        return JsonDocument.Parse(stream);
    }
}
