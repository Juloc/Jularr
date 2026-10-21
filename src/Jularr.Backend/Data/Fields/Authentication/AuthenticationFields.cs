namespace Jularr.Backend.Data.Fields.Authentication
{
    // Transient input fields; plaintext passwords and browser nonces are never database columns.
    public static class AuthenticationFields
    {
        public static readonly StringField Password = new() { AllowBlank = false, MaximumLength = 128 };
        // What a person types to sign in: the username or the email address.
        public static readonly StringField Login = new(NpgsqlTypes.NpgsqlDbType.Citext) { AllowBlank = false, MaximumLength = 254 };

        public static readonly StringField NewPassword = new()
        {
            AllowBlank = false,
            MinimumLength = 12,
            MaximumLength = 128
        };

        public static readonly StringField BrowserNonce = new()
        {
            AllowBlank = false,
            MinimumLength = 43,
            MaximumLength = 43
        };
    }
}
