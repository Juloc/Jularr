using System.Data.Common;
using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Branding;

/// <summary>
/// The one owner of the instance branding (#876), written by Setup and Admin → Appearance alike and read by every surface that shows identity.
/// Surfaces read it on every page, so the small settings value is cached for a short time and a write replaces it at once; the logo bytes are never
/// cached here (the logo endpoint lets the browser do that).
/// </summary>
public sealed class InstanceBrandingStore(IServiceScopeFactory scopes, TimeProvider clock)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly Lock gate = new();
    private (InstanceBrandingSettings Settings, DateTimeOffset At)? cached;

    public async Task<InstanceBrandingSettings> GetAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (cached is { } hit && clock.GetUtcNow() - hit.At < CacheFor)
            {
                return hit.Settings;
            }
        }

        var settings = await WithDbAsync(async (connection, _) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """SELECT "Name", "HueBranding", "Hue", "Logo" IS NOT NULL, "LogoVersion", "RecolourLogo", "LogoRecolourable" FROM "InstanceBranding" WHERE "Id" = 1;""";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? new InstanceBrandingSettings(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetInt16(2), reader.GetBoolean(3), reader.GetInt64(4), reader.GetBoolean(5), reader.GetBoolean(6))
                : InstanceBrandingSettings.Default;
        }, cancellationToken);
        Remember(settings);
        return settings;
    }

    /// <summary>Saves the name, the brand colour switch and colour and whether the logo is recoloured; the logo itself is untouched. The name must already be normalized (<see cref="BrandingValidation.TryNormalizeName"/>).</summary>
    public async Task SaveIdentityAsync(string? name, bool hueBranding, int? hue, bool recolourLogo, CancellationToken cancellationToken)
    {
        if (name is not null && (!BrandingValidation.TryNormalizeName(name, out var normalized) || normalized != name))
        {
            throw new ArgumentException("The instance name is not valid.", nameof(name));
        }

        if (hue is < 0 or > 359)
        {
            throw new ArgumentOutOfRangeException(nameof(hue));
        }

        await UpsertAsync(
            """
            INSERT INTO "InstanceBranding" ("Id", "Name", "HueBranding", "Hue", "RecolourLogo", "UpdatedAt") VALUES (1, @name, @hueBranding, @hue, @recolour, @now)
            ON CONFLICT ("Id") DO UPDATE SET "Name" = excluded."Name", "HueBranding" = excluded."HueBranding", "Hue" = excluded."Hue", "RecolourLogo" = excluded."RecolourLogo", "UpdatedAt" = excluded."UpdatedAt";
            """,
            command =>
            {
                Add(command, "@name", name);
                Add(command, "@hueBranding", hueBranding);
                Add(command, "@hue", hue is { } value ? (short)value : null);
                Add(command, "@recolour", recolourLogo);
            },
            cancellationToken);
    }

    /// <summary>Stores a validated logo and bumps its version, which is what makes browsers load the new one.</summary>
    public async Task<BrandingLogoProblem> SetLogoAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var problem = BrandingValidation.Validate(bytes, out var contentType);
        if (problem != BrandingLogoProblem.None)
        {
            return problem;
        }

        await UpsertAsync(
            """
            INSERT INTO "InstanceBranding" ("Id", "Logo", "LogoContentType", "LogoRecolourable", "LogoVersion", "UpdatedAt") VALUES (1, @logo, @type, @recolourable, 1, @now)
            ON CONFLICT ("Id") DO UPDATE SET "Logo" = excluded."Logo", "LogoContentType" = excluded."LogoContentType", "LogoRecolourable" = excluded."LogoRecolourable", "LogoVersion" = "InstanceBranding"."LogoVersion" + 1, "UpdatedAt" = excluded."UpdatedAt";
            """,
            command =>
            {
                Add(command, "@logo", bytes);
                Add(command, "@type", contentType);
                Add(command, "@recolourable", BrandingValidation.CanRecolour(bytes, contentType));
            },
            cancellationToken);
        return BrandingLogoProblem.None;
    }

    public Task RemoveLogoAsync(CancellationToken cancellationToken) => UpsertAsync(
        """
        UPDATE "InstanceBranding" SET "Logo" = NULL, "LogoContentType" = NULL, "LogoRecolourable" = false, "LogoVersion" = "LogoVersion" + 1, "UpdatedAt" = @now WHERE "Id" = 1;
        """,
        _ => { },
        cancellationToken);

    /// <summary>Back to plain Jularr: the name, the logo and the hue branding all go.</summary>
    public Task ResetAsync(CancellationToken cancellationToken) => UpsertAsync("""DELETE FROM "InstanceBranding" WHERE "Id" = 1;""", _ => { }, cancellationToken);

    public Task<StoredBrandingLogo?> GetLogoAsync(CancellationToken cancellationToken) => WithDbAsync<StoredBrandingLogo?>(async (connection, _) =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT "LogoContentType", "Logo", "LogoVersion" FROM "InstanceBranding" WHERE "Id" = 1 AND "Logo" IS NOT NULL;""";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new StoredBrandingLogo(reader.GetString(0), reader.GetFieldValue<byte[]>(1), reader.GetInt64(2)) : null;
    }, cancellationToken);

    private async Task UpsertAsync(string sql, Action<DbCommand> parameters, CancellationToken cancellationToken)
    {
        await WithDbAsync(async (connection, _) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (sql.Contains("@now", StringComparison.Ordinal))
            {
                Add(command, "@now", clock.GetUtcNow().UtcDateTime);
            }

            parameters(command);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);

        lock (gate)
        {
            cached = null;
        }
    }

    private async Task<T> WithDbAsync<T>(Func<DbConnection, AppDbContext, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            return await action(connection, db);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private void Remember(InstanceBrandingSettings settings)
    {
        lock (gate)
        {
            cached = (settings, clock.GetUtcNow());
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
