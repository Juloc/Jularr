namespace Jularr.Integrations.Data.Codex
{
    // What the Codex CLI says about itself and its stored sign-in; Version and SignIn are the CLI's own short words, never raw output.
    internal sealed class CodexCliStatus(bool installed, string? version, bool connected, string? signIn)
    {
        public bool Installed { get; } = installed;
        public string? Version { get; } = version;
        public bool Connected { get; } = connected;
        public string? SignIn { get; } = signIn;
    }

    // The address and one-time code a device login asks the person to use.
    internal sealed class CodexDeviceCode(Uri verificationUrl, string userCode)
    {
        public Uri VerificationUrl { get; } = verificationUrl;
        public string UserCode { get; } = userCode;
    }
}
