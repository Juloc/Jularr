using Jularr.Backend.Data.Common;

namespace Jularr.Backend.Data.Admin.Ai.V1
{
    public sealed class CodexConnectionParametersV1 : BaseParameters
    {
    }

    // NotInstalled: the image has no runnable Codex CLI. NotConnected: no sign-in stored. LoginPending: a device login waits for the person. Connected: a sign-in is stored.
    public enum CodexPhase : byte
    {
        NotInstalled = 0,
        NotConnected = 1,
        LoginPending = 2,
        Connected = 3
    }

    // The server's one Codex connection as the CLI reports it now. SignIn is chatgpt, apiKey or other; ErrorKey names how the last login ended when it did not succeed.
    public sealed class CodexConnectionV1(CodexPhase phase, string? version, string? signIn, string? verificationUrl, string? userCode, string? errorKey) : BaseResponse
    {
        public CodexPhase Phase { get; } = phase;
        public string? Version { get; } = version;
        public string? SignIn { get; } = signIn;
        public string? VerificationUrl { get; } = verificationUrl;
        public string? UserCode { get; } = userCode;
        public string? ErrorKey { get; } = errorKey;
    }
}
