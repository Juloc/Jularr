using System.Globalization;
using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Progress;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Jularr.Web.Features.Auth;

public sealed class OwnerAuthService(
    AppDbContext db,
    IPasswordHasher<OwnerAccount> passwordHasher)
{
    // Legacy name kept after the Jularr rename: the claim lives inside existing sign-in cookies.
    private const string SessionVersionClaimType = "anilingo:session-version";

    public Task<bool> HasOwnerAsync(CancellationToken cancellationToken = default) =>
        db.OwnerAccounts
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == OwnerAccount.SingletonId
                    && x.Role == AccountRole.Owner,
                cancellationToken);

    public async Task<OwnerAccount> CreateOwnerAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        var cleanedUserName = CleanUserName(userName);
        ValidatePassword(password);

        if (await db.OwnerAccounts.AnyAsync(cancellationToken))
        {
            throw new InvalidOperationException("The Jularr owner account has already been created.");
        }

        var owner = new OwnerAccount
        {
            Id = OwnerAccount.SingletonId,
            UserName = cleanedUserName,
            NormalizedUserName = NormalizeUserName(cleanedUserName),
            Role = AccountRole.Owner,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };
        owner.PasswordHash = passwordHasher.HashPassword(owner, password);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.OwnerAccounts.Add(owner);
        await db.SaveChangesAsync(cancellationToken);
        owner.SessionVersion = await GetSessionVersionAsync(owner.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return owner;
    }

    public Task<OwnerAccount> CreateUserAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default) =>
        CreateUserCoreAsync(userName, password, isEnabled: true, cancellationToken);

    public Task<OwnerAccount> CreateRegistrationRequestAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default) =>
        CreateUserCoreAsync(userName, password, isEnabled: false, cancellationToken);

    private async Task<OwnerAccount> CreateUserCoreAsync(
        string userName,
        string password,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var cleanedUserName = CleanUserName(userName);
        ValidatePassword(password);
        var normalized = NormalizeUserName(cleanedUserName);

        if (await db.OwnerAccounts.AnyAsync(
                x => x.NormalizedUserName == normalized,
                cancellationToken))
        {
            throw new InvalidOperationException("This user name already exists.");
        }

        var account = new OwnerAccount
        {
            Id = Guid.NewGuid().ToString("N"),
            UserName = cleanedUserName,
            NormalizedUserName = normalized,
            Role = AccountRole.User,
            IsEnabled = isEnabled,
            CreatedAt = DateTime.UtcNow
        };
        account.PasswordHash = passwordHasher.HashPassword(account, password);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.OwnerAccounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        account.SessionVersion = await GetSessionVersionAsync(account.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return account;
    }

    public async Task<IReadOnlyList<LocalAccountSummary>> ListAsync(
        CancellationToken cancellationToken = default) =>
        await db.OwnerAccounts
            .AsNoTracking()
            .OrderBy(x => x.Role)
            .ThenBy(x => x.UserName)
            .Select(x => new LocalAccountSummary(
                x.Id,
                x.UserName,
                x.Role,
                x.IsEnabled,
                x.CreatedAt))
            .ToListAsync(cancellationToken);

    public async Task<LocalAccountSummary?> GetAsync(
        string accountId,
        CancellationToken cancellationToken = default) =>
        await db.OwnerAccounts
            .AsNoTracking()
            .Where(x => x.Id == accountId)
            .Select(x => new LocalAccountSummary(
                x.Id,
                x.UserName,
                x.Role,
                x.IsEnabled,
                x.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task RenameAsync(
        string accountId,
        string userName,
        CancellationToken cancellationToken = default)
    {
        var cleanedUserName = CleanUserName(userName);
        var normalized = NormalizeUserName(cleanedUserName);

        var account = await db.OwnerAccounts
            .SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken)
            ?? throw new InvalidOperationException("Account was not found.");

        if (await db.OwnerAccounts.AnyAsync(
                x => x.Id != accountId && x.NormalizedUserName == normalized,
                cancellationToken))
        {
            throw new InvalidOperationException("This user name already exists.");
        }

        account.UserName = cleanedUserName;
        account.NormalizedUserName = normalized;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetEnabledAsync(
        string accountId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var account = await db.OwnerAccounts
            .SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken)
            ?? throw new InvalidOperationException("Account was not found.");

        if (account.Role == AccountRole.Owner && !enabled)
        {
            throw new InvalidOperationException("The owner account cannot be disabled.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        account.IsEnabled = enabled;
        await db.SaveChangesAsync(cancellationToken);

        if (!enabled)
        {
            await BumpSessionVersionAsync(account.Id, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Makes an account a User or a Media manager. There is exactly one Owner: it can't be
    /// demoted and no other account can become Owner. The new role applies on the account's
    /// next request, when cookie validation reads it from the database.
    /// </summary>
    public async Task SetRoleAsync(
        string accountId,
        AccountRole role,
        CancellationToken cancellationToken = default)
    {
        if (role is not (AccountRole.User or AccountRole.MediaManager))
        {
            throw new InvalidOperationException("Only the User and Media manager roles can be assigned.");
        }

        var account = await db.OwnerAccounts
            .SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken)
            ?? throw new InvalidOperationException("Account was not found.");

        if (account.Role == AccountRole.Owner)
        {
            throw new InvalidOperationException("The owner account keeps the Owner role.");
        }

        account.Role = role;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ResetPasswordAsync(
        string accountId,
        string password,
        CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);

        var account = await db.OwnerAccounts
            .SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken)
            ?? throw new InvalidOperationException("Account was not found.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        account.PasswordHash = passwordHasher.HashPassword(account, password);
        await db.SaveChangesAsync(cancellationToken);
        await BumpSessionVersionAsync(account.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task InvalidateSessionsAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var exists = await db.OwnerAccounts
            .AsNoTracking()
            .AnyAsync(x => x.Id == accountId, cancellationToken);
        if (!exists)
        {
            throw new InvalidOperationException("Account was not found.");
        }

        await BumpSessionVersionAsync(accountId, cancellationToken);
    }

    public async Task DeleteUserAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var account = await db.OwnerAccounts
            .SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken)
            ?? throw new InvalidOperationException("Account was not found.");

        if (account.Role == AccountRole.Owner)
        {
            throw new InvalidOperationException("The owner account cannot be deleted.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DeleteProfileDataAsync(account.Id, cancellationToken);
        await new VideoProgressService(db).DeleteProfileDataAsync(account.Id, cancellationToken);
        await new ActiveSessionService(db, TimeProvider.System).DeleteProfileSessionsAsync(account.Id, cancellationToken);
        db.OwnerAccounts.Remove(account);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<OwnerAccount?> ValidateCredentialsAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeUserName(userName);
        var account = await db.OwnerAccounts
            .SingleOrDefaultAsync(
                x => x.NormalizedUserName == normalized,
                cancellationToken);

        if (account is null || !account.IsEnabled)
        {
            return null;
        }

        var result = passwordHasher.VerifyHashedPassword(
            account,
            account.PasswordHash,
            password);
        if (result == PasswordVerificationResult.Failed)
        {
            return null;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            account.PasswordHash = passwordHasher.HashPassword(account, password);
            await db.SaveChangesAsync(cancellationToken);
        }

        account.SessionVersion = await GetSessionVersionAsync(account.Id, cancellationToken);
        return account;
    }

    public Task<OwnerAccount?> GetEnabledAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default) =>
        GetEnabledAccountAsync(
            accountId,
            expectedSessionVersion: null,
            cancellationToken);

    public async Task<OwnerAccount?> GetEnabledAccountAsync(
        string accountId,
        long? expectedSessionVersion,
        CancellationToken cancellationToken = default)
    {
        var account = await db.OwnerAccounts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == accountId && x.IsEnabled,
                cancellationToken);
        if (account is null)
        {
            return null;
        }

        account.SessionVersion = await GetSessionVersionAsync(
            account.Id,
            cancellationToken);

        if (expectedSessionVersion.HasValue &&
            expectedSessionVersion.Value != account.SessionVersion)
        {
            return null;
        }

        return account;
    }

    public static ClaimsPrincipal CreatePrincipal(OwnerAccount account)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, account.Id),
            new Claim(ClaimTypes.Name, account.UserName),
            new Claim(ClaimTypes.Role, account.Role.ToString()),
            new Claim(
                SessionVersionClaimType,
                account.SessionVersion.ToString(CultureInfo.InvariantCulture))
        ],
        CookieAuthenticationDefaults.AuthenticationScheme);

        return new ClaimsPrincipal(identity);
    }

    public static string? GetAccountId(ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier);

    public static long? GetSessionVersion(ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(SessionVersionClaimType);
        return long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var version)
            ? version
            : null;
    }

    private async Task<long> GetSessionVersionAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        await EnsureSessionStateAsync(accountId, cancellationToken);

        return await db.Database
            .SqlQueryRaw<long>(
                """
                SELECT "Version" AS "Value"
                FROM "AccountSessionStates"
                WHERE "AccountId" = {0}
                """,
                accountId)
            .SingleAsync(cancellationToken);
    }

    private async Task<long> BumpSessionVersionAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        await EnsureSessionStateAsync(accountId, cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "AccountSessionStates"
            SET "Version" = "Version" + 1
            WHERE "AccountId" = {0}
            """,
            new object[] { accountId },
            cancellationToken);

        return await GetSessionVersionAsync(accountId, cancellationToken);
    }

    private Task EnsureSessionStateAsync(
        string accountId,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "AccountSessionStates" ("AccountId", "Version")
            VALUES ({0}, 1)
            ON CONFLICT DO NOTHING
            """,
            new object[] { accountId },
            cancellationToken);

    private async Task DeleteProfileDataAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        var profileTables = db.Model
            .GetEntityTypes()
            .Select(entityType =>
            {
                var property = entityType.FindProperty("ProfileId");
                var tableName = entityType.GetTableName();
                if (property?.ClrType != typeof(string) || tableName is null)
                {
                    return null;
                }

                var schema = entityType.GetSchema();
                var storeObject = StoreObjectIdentifier.Table(tableName, schema);
                var columnName = property.GetColumnName(storeObject);
                if (columnName is null)
                {
                    return null;
                }

                return new ProfileTable(schema, tableName, columnName);
            })
            .Where(x => x is not null)
            .Cast<ProfileTable>()
            .Distinct()
            .ToArray();

        foreach (var table in profileTables)
        {
            var qualifiedTable = string.IsNullOrWhiteSpace(table.Schema)
                ? QuoteIdentifier(table.Table)
                : $"{QuoteIdentifier(table.Schema!)}.{QuoteIdentifier(table.Table)}";
            var sql =
                $"DELETE FROM {qualifiedTable} WHERE {QuoteIdentifier(table.Column)} = {{0}}";

            await db.Database.ExecuteSqlRawAsync(
                sql,
                new object[] { profileId },
                cancellationToken);
        }
    }

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string CleanUserName(string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        var cleaned = userName.Trim();

        if (cleaned.Length > 80)
        {
            throw new ArgumentException(
                "User name must be 80 characters or fewer.",
                nameof(userName));
        }

        return cleaned;
    }

    private static string NormalizeUserName(string userName) =>
        (userName ?? string.Empty).Trim().ToUpperInvariant();

    private static void ValidatePassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        if (password.Length < 12)
        {
            throw new ArgumentException(
                "Password must contain at least 12 characters.",
                nameof(password));
        }
    }

    private sealed record ProfileTable(
        string? Schema,
        string Table,
        string Column);
}
