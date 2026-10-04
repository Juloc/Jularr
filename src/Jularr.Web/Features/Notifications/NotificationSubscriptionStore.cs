using System.Collections.Frozen;
using System.Data;
using System.Data.Common;
using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Events;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Notifications;

/// <summary>
/// Canonical profile notification-preference persistence. One explicit event row owns Enabled/Timing,
/// child rows own selected channels, and profile-channel rows own optional channel gates. Missing rows
/// resolve from the canonical event/channel defaults without materializing shadow state.
/// </summary>
public sealed class NotificationSubscriptionStore(AppDbContext db)
{
    public async Task<NotificationEventPreference> GetEventPreferenceAsync(string profileId, JularrEventCategory category, CancellationToken cancellationToken = default)
    {
        profileId = NormalizeProfileId(profileId);
        EnsureCategory(category);

        return await WithConnectionAsync(async connection =>
        {
            bool? enabled = null;
            NotificationDeliveryTiming timing = default;
            DateTime? updatedAtUtc = null;

            await using (var preferenceCommand = connection.CreateCommand())
            {
                preferenceCommand.CommandText =
                    """
                    SELECT "Enabled", "Timing", "UpdatedAtUtc"
                    FROM "NotificationSubscriptions"
                    WHERE "ProfileId" = @profileId AND "Category" = @category;
                    """;
                Add(preferenceCommand, "@profileId", profileId);
                Add(preferenceCommand, "@category", (int)category);

                await using var reader = await preferenceCommand.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    enabled = reader.GetBoolean(0);
                    timing = ReadTiming(reader.GetValue(1));
                    updatedAtUtc = ParseUtc(reader.GetValue(2));
                }
            }

            if (enabled is null)
            {
                return NotificationEventPreference.FromDefault(profileId, category);
            }

            var channels = new HashSet<NotificationChannel>();
            await using (var channelCommand = connection.CreateCommand())
            {
                channelCommand.CommandText =
                    """
                    SELECT "Channel"
                    FROM "NotificationSubscriptionChannels"
                    WHERE "ProfileId" = @profileId AND "Category" = @category
                    ORDER BY "Channel";
                    """;
                Add(channelCommand, "@profileId", profileId);
                Add(channelCommand, "@category", (int)category);

                await using var reader = await channelCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    channels.Add(ReadChannel(reader.GetValue(0)));
                }
            }

            return new NotificationEventPreference(profileId, category, enabled.Value, channels.ToFrozenSet(), timing, updatedAtUtc, true);
        }, cancellationToken);
    }

    /// <summary>Returns every catalog category, resolving missing explicit rows from its built-in event policy.</summary>
    public async Task<IReadOnlyDictionary<JularrEventCategory, NotificationEventPreference>> GetAllEventPreferencesAsync(string profileId, CancellationToken cancellationToken = default)
    {
        profileId = NormalizeProfileId(profileId);

        return await WithConnectionAsync(async connection =>
        {
            var saved = new Dictionary<JularrEventCategory, (bool Enabled, NotificationDeliveryTiming Timing, DateTime UpdatedAtUtc)>();
            await using (var preferenceCommand = connection.CreateCommand())
            {
                preferenceCommand.CommandText =
                    """
                    SELECT "Category", "Enabled", "Timing", "UpdatedAtUtc"
                    FROM "NotificationSubscriptions"
                    WHERE "ProfileId" = @profileId;
                    """;
                Add(preferenceCommand, "@profileId", profileId);

                await using var reader = await preferenceCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var category = ReadCategory(reader.GetValue(0));
                    saved[category] = (reader.GetBoolean(1), ReadTiming(reader.GetValue(2)), ParseUtc(reader.GetValue(3)));
                }
            }

            var channelsByCategory = new Dictionary<JularrEventCategory, HashSet<NotificationChannel>>();
            await using (var channelCommand = connection.CreateCommand())
            {
                channelCommand.CommandText =
                    """
                    SELECT "Category", "Channel"
                    FROM "NotificationSubscriptionChannels"
                    WHERE "ProfileId" = @profileId
                    ORDER BY "Category", "Channel";
                    """;
                Add(channelCommand, "@profileId", profileId);

                await using var reader = await channelCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var category = ReadCategory(reader.GetValue(0));
                    if (!channelsByCategory.TryGetValue(category, out var channels))
                    {
                        channels = [];
                        channelsByCategory[category] = channels;
                    }

                    channels.Add(ReadChannel(reader.GetValue(1)));
                }
            }

            var result = new Dictionary<JularrEventCategory, NotificationEventPreference>();
            foreach (var category in Enum.GetValues<JularrEventCategory>())
            {
                if (!saved.TryGetValue(category, out var explicitPreference))
                {
                    result[category] = NotificationEventPreference.FromDefault(profileId, category);
                    continue;
                }

                var channels = channelsByCategory.TryGetValue(category, out var selectedChannels) ? selectedChannels.ToFrozenSet() : Array.Empty<NotificationChannel>().ToFrozenSet();
                result[category] = new NotificationEventPreference(profileId, category, explicitPreference.Enabled, channels, explicitPreference.Timing, explicitPreference.UpdatedAtUtc, true);
            }

            return result;
        }, cancellationToken);
    }

    public Task SetEventPreferenceAsync(string profileId, JularrEventCategory category, NotificationEventPreferenceUpdate update, CancellationToken cancellationToken = default)
    {
        profileId = NormalizeProfileId(profileId);
        EnsureCategory(category);
        NotificationPreferencePolicy.Validate(category, update);

        var channels = update.Channels.OrderBy(channel => channel).ToArray();
        var now = DateTime.UtcNow;

        return WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var preferenceCommand = connection.CreateCommand())
            {
                preferenceCommand.Transaction = transaction;
                preferenceCommand.CommandText =
                    """
                    INSERT INTO "NotificationSubscriptions" ("ProfileId", "Category", "Enabled", "Timing", "UpdatedAtUtc")
                    VALUES (@profileId, @category, @enabled, @timing, @now)
                    ON CONFLICT ("ProfileId", "Category") DO UPDATE SET
                        "Enabled" = excluded."Enabled",
                        "Timing" = excluded."Timing",
                        "UpdatedAtUtc" = excluded."UpdatedAtUtc";
                    """;
                Add(preferenceCommand, "@profileId", profileId);
                Add(preferenceCommand, "@category", (int)category);
                Add(preferenceCommand, "@enabled", update.Enabled);
                Add(preferenceCommand, "@timing", (int)update.Timing);
                Add(preferenceCommand, "@now", now);
                await preferenceCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteChannels = connection.CreateCommand())
            {
                deleteChannels.Transaction = transaction;
                deleteChannels.CommandText =
                    """
                    DELETE FROM "NotificationSubscriptionChannels"
                    WHERE "ProfileId" = @profileId AND "Category" = @category;
                    """;
                Add(deleteChannels, "@profileId", profileId);
                Add(deleteChannels, "@category", (int)category);
                await deleteChannels.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var channel in channels)
            {
                await using var insertChannel = connection.CreateCommand();
                insertChannel.Transaction = transaction;
                insertChannel.CommandText =
                    """
                    INSERT INTO "NotificationSubscriptionChannels" ("ProfileId", "Category", "Channel")
                    VALUES (@profileId, @category, @channel);
                    """;
                Add(insertChannel, "@profileId", profileId);
                Add(insertChannel, "@category", (int)category);
                Add(insertChannel, "@channel", (int)channel);
                await insertChannel.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task ResetEventPreferenceAsync(string profileId, JularrEventCategory category, CancellationToken cancellationToken = default)
    {
        profileId = NormalizeProfileId(profileId);
        EnsureCategory(category);

        return WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var deleteChannels = connection.CreateCommand())
            {
                deleteChannels.Transaction = transaction;
                deleteChannels.CommandText =
                    """
                    DELETE FROM "NotificationSubscriptionChannels"
                    WHERE "ProfileId" = @profileId AND "Category" = @category;
                    """;
                Add(deleteChannels, "@profileId", profileId);
                Add(deleteChannels, "@category", (int)category);
                await deleteChannels.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deletePreference = connection.CreateCommand())
            {
                deletePreference.Transaction = transaction;
                deletePreference.CommandText =
                    """
                    DELETE FROM "NotificationSubscriptions"
                    WHERE "ProfileId" = @profileId AND "Category" = @category;
                    """;
                Add(deletePreference, "@profileId", profileId);
                Add(deletePreference, "@category", (int)category);
                await deletePreference.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<NotificationChannel, NotificationProfileChannelPreference>> GetProfileChannelPreferencesAsync(string profileId, CancellationToken cancellationToken = default)
    {
        profileId = NormalizeProfileId(profileId);

        var saved = await WithConnectionAsync(async connection =>
        {
            var rows = new Dictionary<NotificationChannel, NotificationProfileChannelPreference>();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT "Channel", "Enabled", "UpdatedAtUtc"
                FROM "NotificationProfileChannels"
                WHERE "ProfileId" = @profileId;
                """;
            Add(command, "@profileId", profileId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var channel = ReadChannel(reader.GetValue(0));
                rows[channel] = new NotificationProfileChannelPreference(profileId, channel, reader.GetBoolean(1), ParseUtc(reader.GetValue(2)), true);
            }

            return rows;
        }, cancellationToken);

        var result = new Dictionary<NotificationChannel, NotificationProfileChannelPreference>();
        foreach (var channel in Enum.GetValues<NotificationChannel>())
        {
            result[channel] = saved.TryGetValue(channel, out var preference) ? preference : NotificationProfileChannelPreference.FromDefault(profileId, channel);
        }

        return result;
    }

    public Task SetProfileChannelEnabledAsync(string profileId, NotificationChannel channel, bool enabled, CancellationToken cancellationToken = default)
    {
        profileId = NormalizeProfileId(profileId);
        EnsureChannel(channel);
        var now = DateTime.UtcNow;

        return WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "NotificationProfileChannels" ("ProfileId", "Channel", "Enabled", "UpdatedAtUtc")
                VALUES (@profileId, @channel, @enabled, @now)
                ON CONFLICT ("ProfileId", "Channel") DO UPDATE SET
                    "Enabled" = excluded."Enabled",
                    "UpdatedAtUtc" = excluded."UpdatedAtUtc";
                """;
            Add(command, "@profileId", profileId);
            Add(command, "@channel", (int)channel);
            Add(command, "@enabled", enabled);
            Add(command, "@now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    private static string NormalizeProfileId(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return profileId.Trim();
    }

    private static void EnsureCategory(JularrEventCategory category)
    {
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown notification event category.");
        }
    }

    private static void EnsureChannel(NotificationChannel channel)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown notification channel.");
        }
    }

    private static JularrEventCategory ReadCategory(object value)
    {
        var category = (JularrEventCategory)Convert.ToInt32(value, CultureInfo.InvariantCulture);
        EnsureCategory(category);
        return category;
    }

    private static NotificationChannel ReadChannel(object value)
    {
        var channel = (NotificationChannel)Convert.ToInt32(value, CultureInfo.InvariantCulture);
        EnsureChannel(channel);
        return channel;
    }

    private static NotificationDeliveryTiming ReadTiming(object value)
    {
        var timing = (NotificationDeliveryTiming)Convert.ToInt32(value, CultureInfo.InvariantCulture);
        if (!Enum.IsDefined(timing))
        {
            throw new InvalidOperationException($"Stored notification timing value {(int)timing} is invalid.");
        }

        return timing;
    }

    private static DateTime ParseUtc(object value)
    {
        if (value is DateTime dateTime)
        {
            return dateTime.Kind == DateTimeKind.Utc ? dateTime : dateTime.ToUniversalTime();
        }

        if (value is DateTimeOffset dateTimeOffset)
        {
            return dateTimeOffset.UtcDateTime;
        }

        return DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).UtcDateTime;
    }

    private async Task WithConnectionAsync(Func<DbConnection, Task> action, CancellationToken cancellationToken)
    {
        await WithConnectionAsync(async connection =>
        {
            await action(connection);
            return true;
        }, cancellationToken);
    }

    private async Task<T> WithConnectionAsync<T>(Func<DbConnection, Task<T>> action, CancellationToken cancellationToken)
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

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
