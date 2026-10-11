namespace Jularr.Backend.Data.Enums.Account
{
    public enum AuthChallengePurposeType : byte
    {
        EmailVerification = 1,
        PasswordReset = 2,
        TotpEnrollment = 3,
        TotpSignIn = 4,
        PasskeyEnrollment = 5,
        PasskeySignIn = 6,
        ExternalLogin = 7,
        ExternalLink = 8,
        ProviderMediaConsent = 9
    }
}
