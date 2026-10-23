namespace Jularr.Backend.Data.Fields.Profile
{
    public static class ProfilePinFields
    {
        // Three to six digits; only its hash is stored.
        public static readonly StringField Pin = new() { MinimumLength = 3, MaximumLength = 6, AllowBlank = false };
    }
}
