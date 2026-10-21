using Jularr.Backend.Data.Common;

namespace Jularr.Backend.Logic.Account
{
    // The one rule for a new password: at least 12 characters and at least three of the four kinds
    // (lowercase letters, uppercase letters, digits, special characters).
    internal static class AccountPasswordPolicy
    {
        public const int MinimumLength = 12;
        public const int RequiredKinds = 3;

        public static bool IsStrong(string password)
        {
            if (password.EnumerateRunes().Count() < MinimumLength) return false;

            var lower = false;
            var upper = false;
            var digit = false;
            var special = false;
            foreach (var rune in password.EnumerateRunes())
            {
                if (System.Text.Rune.IsLower(rune)) lower = true;
                else if (System.Text.Rune.IsUpper(rune)) upper = true;
                else if (System.Text.Rune.IsDigit(rune)) digit = true;
                else special = true;
            }

            return new[] { lower, upper, digit, special }.Count(kind => kind) >= RequiredKinds;
        }

        // Throws a validation error on the password field.
        public static void Require(string password, string field)
        {
            if (!IsStrong(password))
            {
                throw new DataValidationException(field, "password_weak");
            }
        }
    }
}
