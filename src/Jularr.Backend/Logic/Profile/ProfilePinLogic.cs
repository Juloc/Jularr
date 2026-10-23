using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Infrastructure.Sql;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Logic.Profile
{
    internal static class ProfilePinLogic
    {
        private static readonly PasswordHasher<ProfilePinData> Hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 100_000 }));

        public static bool VerifyPin(string pinHash, string pin) =>
            IsDigits(pin) && Hasher.VerifyHashedPassword(null!, pinHash, pin) != PasswordVerificationResult.Failed;

        public static async Task UpdateProfilePinAsync(this LogicContext ctx, long profileId, string? pin, CancellationToken ct)
        {
            if (pin is not null && !IsDigits(pin))
            {
                throw new DataValidationException("Pin", "invalid_pin_format");
            }

            // A new or removed PIN starts without failures.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Profiles" AS profile
                SET
                    "PinHash" = @PinHash,
                    "PinLength" = @PinLength,
                    "PinFailedCount" = 0,
                    "PinLockedUntil" = NULL,
                    "UpdatedAt" = statement_timestamp()
                WHERE
                    profile."Id" = @ProfileId;
                """;
            sql.Parameters.Add(new ProfilePinData(profileId, pin is null ? null : Hasher.HashPassword(null!, pin), pin?.Length));
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The profile was not found.");
            }
        }

        public static async Task RecordPinAttemptAsync(this LogicContext ctx, long profileId, bool isValid, CancellationToken ct)
        {
            // The fifth failure locks for a minute and starts counting anew.
            var sql = new SqlStatement();
            sql.Statement = isValid
                ? """
                  UPDATE
                      "Profiles" AS profile
                  SET
                      "PinFailedCount" = 0,
                      "PinLockedUntil" = NULL
                  WHERE
                      profile."Id" = @ProfileId;
                  """
                : """
                  UPDATE
                      "Profiles" AS profile
                  SET
                      "PinFailedCount" = CASE WHEN profile."PinFailedCount" >= 4 THEN 0 ELSE profile."PinFailedCount" + 1 END,
                      "PinLockedUntil" = CASE WHEN profile."PinFailedCount" >= 4 THEN statement_timestamp() + interval '1 minute' ELSE profile."PinLockedUntil" END
                  WHERE
                      profile."Id" = @ProfileId;
                  """;
            sql.Parameters.Add(new ProfilePinData(profileId, null, null));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        private static bool IsDigits(string pin) => pin.Length is >= 3 and <= 6 && pin.All(char.IsAsciiDigit);
    }
}
