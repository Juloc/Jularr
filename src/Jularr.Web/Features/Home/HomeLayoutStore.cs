using System.Data;
using System.Data.Common;
using Jularr.Web.Data;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Home;

/// <summary>How a profile left the first-login onboarding.</summary>
public enum HomeOnboardingState
{
    Pending,
    Completed,
    Skipped
}

/// <summary>What is stored for one profile: its onboarding state and its override, null while the instance default applies.</summary>
public sealed record ProfileHomeState(HomeOnboardingState Onboarding, HomeLayoutPreference? Override);

/// <summary>
/// The single owner of the Home/Discover layout: the instance default (<c>InstanceHomeDefaults</c>) and the profile override (<c>ProfileHomeLayouts</c>).
/// A profile without an override follows whatever the instance default is at that moment; saving creates the override and a reset removes it. Neither
/// side ever writes the other. Theme and accent stay with <see cref="Appearance.ProfileAppearanceStore"/>.
/// </summary>
public sealed class HomeLayoutStore(AppDbContext db)
{
    public async Task<HomeLayoutPreference> GetInstanceDefaultAsync(CancellationToken cancellationToken)
    {
        return await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """SELECT "MediaOrder", "HiddenMediaTypes", "LandingPage", "PrioritizeContinue" FROM "InstanceHomeDefaults" WHERE "Id" = 1;""";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? Read(reader) : HomeLayoutPreference.BuiltIn;
        }, cancellationToken);
    }

    public async Task SaveInstanceDefaultAsync(HomeLayoutPreference preference, CancellationToken cancellationToken)
    {
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "InstanceHomeDefaults" ("Id", "MediaOrder", "HiddenMediaTypes", "LandingPage", "PrioritizeContinue", "UpdatedAt")
                VALUES (1, @order, @hidden, @landing, @prioritize, @updatedAt)
                ON CONFLICT("Id") DO UPDATE SET
                    "MediaOrder" = excluded."MediaOrder",
                    "HiddenMediaTypes" = excluded."HiddenMediaTypes",
                    "LandingPage" = excluded."LandingPage",
                    "PrioritizeContinue" = excluded."PrioritizeContinue",
                    "UpdatedAt" = excluded."UpdatedAt";
                """;
            AddLayout(command, preference);
            Add(command, "@updatedAt", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task<ProfileHomeState> GetProfileAsync(string? profileId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return new ProfileHomeState(HomeOnboardingState.Skipped, null);
        }

        return await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """SELECT "OnboardingState", "MediaOrder", "HiddenMediaTypes", "LandingPage", "PrioritizeContinue" FROM "ProfileHomeLayouts" WHERE "ProfileId" = @profileId;""";
            Add(command, "@profileId", profileId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new ProfileHomeState(HomeOnboardingState.Pending, null);
            }

            var onboarding = reader.GetString(0) == "completed" ? HomeOnboardingState.Completed : HomeOnboardingState.Skipped;
            return new ProfileHomeState(onboarding, reader.IsDBNull(1) ? null : new HomeLayoutPreference(Split(reader.GetString(1)), Split(reader.GetString(2)), ParseLanding(reader.GetString(3)), reader.GetBoolean(4)));
        }, cancellationToken);
    }

    /// <summary>Stores the profile's override and marks its onboarding as completed.</summary>
    public async Task SaveProfileAsync(string profileId, HomeLayoutPreference preference, CancellationToken cancellationToken)
    {
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "ProfileHomeLayouts" ("ProfileId", "OnboardingState", "MediaOrder", "HiddenMediaTypes", "LandingPage", "PrioritizeContinue", "UpdatedAt")
                VALUES (@profileId, 'completed', @order, @hidden, @landing, @prioritize, @updatedAt)
                ON CONFLICT("ProfileId") DO UPDATE SET
                    "OnboardingState" = 'completed',
                    "MediaOrder" = excluded."MediaOrder",
                    "HiddenMediaTypes" = excluded."HiddenMediaTypes",
                    "LandingPage" = excluded."LandingPage",
                    "PrioritizeContinue" = excluded."PrioritizeContinue",
                    "UpdatedAt" = excluded."UpdatedAt";
                """;
            Add(command, "@profileId", profileId);
            AddLayout(command, preference);
            Add(command, "@updatedAt", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    /// <summary>Leaves the onboarding without a choice: the instance default keeps applying. A profile that already finished it is not touched.</summary>
    public async Task SkipOnboardingAsync(string profileId, CancellationToken cancellationToken)
    {
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "ProfileHomeLayouts" ("ProfileId", "OnboardingState", "UpdatedAt") VALUES (@profileId, 'skipped', @updatedAt)
                ON CONFLICT("ProfileId") DO NOTHING;
                """;
            Add(command, "@profileId", profileId);
            Add(command, "@updatedAt", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    /// <summary>Removes the override so the current instance default applies again; the onboarding is not offered a second time.</summary>
    public async Task ResetProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "ProfileHomeLayouts" ("ProfileId", "OnboardingState", "UpdatedAt") VALUES (@profileId, 'completed', @updatedAt)
                ON CONFLICT("ProfileId") DO UPDATE SET
                    "MediaOrder" = NULL, "HiddenMediaTypes" = NULL, "LandingPage" = NULL, "PrioritizeContinue" = NULL, "UpdatedAt" = excluded."UpdatedAt";
                """;
            Add(command, "@profileId", profileId);
            Add(command, "@updatedAt", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    /// <summary>The layout a profile sees: its override when it has one, otherwise the instance default, limited to the media types it can use.</summary>
    public async Task<EffectiveHomeLayout> ResolveAsync(string? profileId, IReadOnlySet<WorkMediaType> available, CancellationToken cancellationToken)
    {
        var profile = await GetProfileAsync(profileId, cancellationToken);
        var preference = profile.Override ?? await GetInstanceDefaultAsync(cancellationToken);
        return HomeLayoutPolicy.Resolve(preference, available, profile.Override is not null, profile.Onboarding);
    }

    /// <summary>Where a visit without a destination lands: the viewer's own choice, else the instance default.</summary>
    public async Task<HomeLanding> GetLandingAsync(string profileId, CancellationToken cancellationToken) =>
        ((await GetProfileAsync(profileId, cancellationToken)).Override ?? await GetInstanceDefaultAsync(cancellationToken)).Landing;

    /// <summary>The media types Home/Discover can order, reduced to what a viewer may use.</summary>
    public static IReadOnlySet<WorkMediaType> Orderable(IEnumerable<WorkMediaType> visible) =>
        visible.Where(DiscoveryShelfComposer.SupportedMediaTypes.Contains).ToHashSet();

    /// <summary>The media types an instance with these module switches serves and Home can arrange; Admin → Instance and Setup both offer exactly these.</summary>
    public static IReadOnlySet<WorkMediaType> OrderableFor(InstanceModuleSettings modules) =>
        Orderable(WorkMediaTypes.All.Where(type => InstanceModuleMedia.IsCapabilityFamilyEnabled(modules, type)));

    private static HomeLayoutPreference Read(DbDataReader reader) => new(Split(reader.GetString(0)), Split(reader.GetString(1)), ParseLanding(reader.GetString(2)), reader.GetBoolean(3));

    private static string[] Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static HomeLanding ParseLanding(string value) => value == "library" ? HomeLanding.Library : HomeLanding.Home;

    private static void AddLayout(DbCommand command, HomeLayoutPreference preference)
    {
        Add(command, "@order", string.Join(',', preference.MediaOrder));
        Add(command, "@hidden", string.Join(',', preference.Hidden));
        Add(command, "@landing", preference.Landing == HomeLanding.Library ? "library" : "home");
        Add(command, "@prioritize", preference.PrioritizeContinue);
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

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
