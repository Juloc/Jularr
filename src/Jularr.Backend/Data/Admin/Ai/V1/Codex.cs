using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

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

    // The Owner's Codex choices. Empty Model/Effort leave Codex's own default; 0 tokens is unlimited.
    public sealed class CodexSettingsV1(string? model, string? effort, string? fallbackModel, long dailyTokens, long runTokens, int warnPercent) : BaseResponse
    {
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CodexModel))]
        public string? Model { get; } = model;

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CodexEffort))]
        public string? Effort { get; } = effort;

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CodexModel))]
        public string? FallbackModel { get; } = fallbackModel;

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CodexTokens))]
        public long DailyTokens { get; } = dailyTokens;

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CodexTokens))]
        public long RunTokens { get; } = runTokens;

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CodexWarnPercent))]
        public int WarnPercent { get; } = warnPercent;
    }

    public sealed class CodexSettingsDataV1(string? model, string? effort, string? fallbackModel, long dailyTokens, long runTokens, int warnPercent) : BaseData
    {
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CodexModel))]
        public string? Model { get; } = model;

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CodexEffort))]
        public string? Effort { get; } = effort;

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CodexModel))]
        public string? FallbackModel { get; } = fallbackModel;

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CodexTokens))]
        public long DailyTokens { get; } = dailyTokens;

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CodexTokens))]
        public long RunTokens { get; } = runTokens;

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CodexWarnPercent))]
        public int WarnPercent { get; } = warnPercent;
    }

    public sealed class CodexModelV1(string id, string name, string[] efforts, string? defaultEffort, bool isDefault)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string[] Efforts { get; } = efforts;
        public string? DefaultEffort { get; } = defaultEffort;
        public bool IsDefault { get; } = isDefault;
    }

    public sealed class CodexUsageWindowV1(double usedPercent, int? minutes, DateTimeOffset? resetsAt)
    {
        public double UsedPercent { get; } = usedPercent;
        public int? Minutes { get; } = minutes;
        public DateTimeOffset? ResetsAt { get; } = resetsAt;
    }

    // What the signed-in account offers now: its models and the ChatGPT plan's usage windows. Available is false when Codex could not be asked (try again later).
    // UsedToday (UTC day) and UsedSinceStart are the tokens Jularr's own Codex translation used, counted against the Owner's limits.
    public sealed class CodexCatalogV1(bool available, CodexModelV1[] models, string? plan, CodexUsageWindowV1[] windows, long usedToday, long usedSinceStart) : BaseResponse
    {
        public long UsedToday { get; } = usedToday;
        public long UsedSinceStart { get; } = usedSinceStart;
        public bool Available { get; } = available;
        public CodexModelV1[] Models { get; } = models;
        public string? Plan { get; } = plan;
        public CodexUsageWindowV1[] Windows { get; } = windows;
    }
}
