using System.Text;
using Jularr.Web.Features.ReaderCore;

namespace Jularr.Web.Features.ReaderPreferences;

public static class ReaderPreferenceScopes
{
    public const int DefaultGenrePriority = 500;

    public static string Type(ReaderContentType contentType) =>
        $"type:{ReaderContentTypes.ToKey(contentType)}";

    public static string Genre(string genre, int priority = DefaultGenrePriority) =>
        $"genre:{Math.Clamp(priority, 0, 999):000}:{NormalizeGenreKey(genre)}";

    public static string Work(Guid novelWorkId) => $"work:{novelWorkId:N}";

    public static string Work(long workId) => $"work:{workId}";

    public static string ResolveTarget(
        string? target,
        ReaderContentType contentType,
        Guid workId,
        string? genre = null,
        int genrePriority = DefaultGenrePriority) =>
        target?.Trim().ToLowerInvariant() switch
        {
            "default" or "global" => ReaderPreferenceRules.UserDefaultScope,
            "type" => Type(contentType),
            "genre" when !string.IsNullOrWhiteSpace(genre) =>
                Genre(genre, genrePriority),
            "book" or "work" or null or "" => Work(workId),
            _ => throw new InvalidOperationException("Unknown reader preference target.")
        };

    public static bool TryParseGenre(
        string? scopeKey,
        out int priority,
        out string genreKey)
    {
        priority = DefaultGenrePriority;
        genreKey = "";

        if (string.IsNullOrWhiteSpace(scopeKey) ||
            !scopeKey.StartsWith("genre:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = scopeKey["genre:".Length..];
        var parts = remainder.Split(':', 2, StringSplitOptions.TrimEntries);

        if (parts.Length == 2 && int.TryParse(parts[0], out var parsedPriority))
        {
            priority = Math.Clamp(parsedPriority, 0, 999);
            genreKey = NormalizeGenreKey(parts[1]);
        }
        else
        {
            genreKey = NormalizeGenreKey(remainder);
        }

        return genreKey.Length > 0;
    }

    public static string NormalizeGenreKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "other";
        }

        var builder = new StringBuilder(value.Length);
        var pendingDash = false;

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(character);
                pendingDash = false;
            }
            else
            {
                pendingDash = builder.Length > 0;
            }
        }

        return builder.Length == 0
            ? "other"
            : builder.ToString().Trim('-');
    }

    public static string DisplayName(string scopeKey) =>
        scopeKey switch
        {
            ReaderPreferenceRules.UserDefaultScope => "Global",
            _ when scopeKey.StartsWith("type:", StringComparison.OrdinalIgnoreCase) =>
                $"Type: {scopeKey["type:".Length..]}",
            _ when TryParseGenre(scopeKey, out _, out var genre) =>
                $"Genre: {genre}",
            _ when scopeKey.StartsWith("work:", StringComparison.OrdinalIgnoreCase) =>
                "This book",
            _ => scopeKey
        };
}
