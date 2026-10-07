using System.Data;
using System.Data.Common;
using System.Globalization;
using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Appearance;

/// <summary>
/// A profile's appearance: theme mode, optional accent seed (null = default Jularr lila) and the
/// Sakura particle effect density (#387).
/// </summary>
public sealed record ProfileAppearance(
    string ThemeMode,
    string? AccentColor,
    string SakuraMode,
    string? ThemeId = null,
    bool ShowAdminShortcut = true)
{
    public static ProfileAppearance Default { get; } = new(AppTheme.System, null, AppSakura.Default);

    public AccentPalette Palette => AccentPalette.Build(AccentColor);
}

/// <summary>
/// The single place that persists per-profile appearance (the <c>UiProfileThemes</c> row).
/// </summary>
public sealed class ProfileAppearanceStore(AppDbContext db)
{
    public async Task<ProfileAppearance> GetAsync(
        string? profileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return ProfileAppearance.Default;
        }

        return await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT "ThemeMode", "AccentColor", "SakuraMode", "ThemeId", "ShowAdminShortcut"
                FROM "UiProfileThemes"
                WHERE "ProfileId" = @profileId
                LIMIT 1;
                """;
            Add(command, "@profileId", profileId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return ProfileAppearance.Default;
            }

            var mode = AppTheme.NormalizeOrSystem(reader.IsDBNull(0) ? null : reader.GetString(0));
            var accent = reader.IsDBNull(1) ? null : reader.GetString(1);
            var sakura = AppSakura.NormalizeOrDefault(reader.IsDBNull(2) ? null : reader.GetString(2));
            return new ProfileAppearance(
                mode,
                AppAccent.TryNormalize(accent, out var normalized) ? normalized : null,
                sakura,
                reader.IsDBNull(3) ? null : ThemeCatalog.NormalizeOrOriginal(reader.GetString(3)),
                reader.GetBoolean(4));
        }, cancellationToken);
    }

    public async Task SetThemeAsync(
        string profileId,
        string theme,
        CancellationToken cancellationToken)
    {
        RequireProfile(profileId);
        if (!AppTheme.TryNormalize(theme, out var normalized))
        {
            throw new ArgumentException("Theme must be system, light or dark.", nameof(theme));
        }

        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "UiProfileThemes" ("ProfileId", "ThemeMode", "UpdatedAt")
                VALUES (@profileId, @theme, @updatedAt)
                ON CONFLICT("ProfileId") DO UPDATE SET
                    "ThemeMode" = excluded."ThemeMode",
                    "UpdatedAt" = excluded."UpdatedAt";
                """;
            Add(command, "@profileId", profileId);
            Add(command, "@theme", normalized);
            Add(command, "@updatedAt", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task SetSakuraAsync(
        string profileId,
        string sakuraMode,
        CancellationToken cancellationToken)
    {
        RequireProfile(profileId);
        if (!AppSakura.TryNormalize(sakuraMode, out var normalized))
        {
            throw new ArgumentException("Sakura mode must be off, subtle or full.", nameof(sakuraMode));
        }

        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "UiProfileThemes" ("ProfileId", "ThemeMode", "SakuraMode", "UpdatedAt")
                VALUES (@profileId, 'system', @sakura, @updatedAt)
                ON CONFLICT("ProfileId") DO UPDATE SET
                    "SakuraMode" = excluded."SakuraMode",
                    "UpdatedAt" = excluded."UpdatedAt";
                """;
            Add(command, "@profileId", profileId);
            Add(command, "@sakura", normalized);
            Add(command, "@updatedAt", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    /// <summary>Stores whether the sidebar shows the permanent Admin shortcut; Admin stays reachable from the account menu either way.</summary>
    public async Task SetAdminShortcutAsync(string profileId, bool show, CancellationToken cancellationToken)
    {
        RequireProfile(profileId);
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "UiProfileThemes" ("ProfileId", "ThemeMode", "ShowAdminShortcut", "UpdatedAt")
                VALUES (@profileId, 'system', @show, @updatedAt)
                ON CONFLICT("ProfileId") DO UPDATE SET
                    "ShowAdminShortcut" = excluded."ShowAdminShortcut",
                    "UpdatedAt" = excluded."UpdatedAt";
                """;
            Add(command, "@profileId", profileId);
            Add(command, "@show", show);
            Add(command, "@updatedAt", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    /// <summary>Stores an optional profile theme. A null value follows the instance default.</summary>
    public async Task SetThemeIdAsync(
        string profileId,
        string? themeId,
        CancellationToken cancellationToken)
    {
        RequireProfile(profileId);
        if (themeId is not null && !ThemeCatalog.TryGet(themeId, out _))
        {
            throw new ArgumentException("Unknown application theme.", nameof(themeId));
        }

        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "UiProfileThemes" ("ProfileId", "ThemeMode", "ThemeId", "UpdatedAt")
                VALUES (@profileId, 'system', @themeId, @updatedAt)
                ON CONFLICT("ProfileId") DO UPDATE SET
                    "ThemeId" = excluded."ThemeId",
                    "UpdatedAt" = excluded."UpdatedAt";
                """;
            Add(command, "@profileId", profileId);
            Add(command, "@themeId", themeId is null ? null : ThemeCatalog.NormalizeOrOriginal(themeId));
            Add(command, "@updatedAt", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    /// <summary>Stores the accent seed; an empty value resets the profile to the default lila.</summary>
    public async Task<string?> SetAccentAsync(
        string profileId,
        string? accent,
        CancellationToken cancellationToken)
    {
        RequireProfile(profileId);
        if (!AppAccent.TryNormalize(accent, out var normalized))
        {
            throw new ArgumentException("Accent must be a #rrggbb colour.", nameof(accent));
        }

        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "UiProfileThemes" ("ProfileId", "ThemeMode", "AccentColor", "UpdatedAt")
                VALUES (@profileId, 'system', @accent, @updatedAt)
                ON CONFLICT("ProfileId") DO UPDATE SET
                    "AccentColor" = excluded."AccentColor",
                    "UpdatedAt" = excluded."UpdatedAt";
                """;
            Add(command, "@profileId", profileId);
            Add(command, "@accent", normalized);
            Add(command, "@updatedAt", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);

        return normalized;
    }

    private async Task<T> WithConnectionAsync<T>(
        Func<DbConnection, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            return await action(connection);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static void RequireProfile(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("Profile ID is required.", nameof(profileId));
        }
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value switch
        {
            null => DBNull.Value,
            DateTime timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
            _ => value
        };
        command.Parameters.Add(parameter);
    }
}
