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

    // A model the signed-in account may use, with the reasoning efforts it offers.
    internal sealed class CodexModel(string id, string name, string[] efforts, string? defaultEffort, bool isDefault)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string[] Efforts { get; } = efforts;
        public string? DefaultEffort { get; } = defaultEffort;
        public bool IsDefault { get; } = isDefault;
    }

    // One usage window of the ChatGPT plan: how much is used, how long the window is, when it resets.
    internal sealed class CodexUsageWindow(double usedPercent, int? minutes, DateTimeOffset? resetsAt)
    {
        public double UsedPercent { get; } = usedPercent;
        public int? Minutes { get; } = minutes;
        public DateTimeOffset? ResetsAt { get; } = resetsAt;
    }

    // The plan's windows (usually a short and a weekly one); null where the account has no limits to show.
    internal sealed class CodexUsage(string? plan, CodexUsageWindow[] windows)
    {
        public string? Plan { get; } = plan;
        public CodexUsageWindow[] Windows { get; } = windows;
    }

    // What the app-server told about the account: the models and the plan usage (null when the usage could not be read).
    internal sealed class CodexCatalog(CodexModel[] models, CodexUsage? usage)
    {
        public CodexModel[] Models { get; } = models;
        public CodexUsage? Usage { get; } = usage;
    }
}
