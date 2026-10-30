using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;
using Jularr.Backend.Data.Fields.Translation;

namespace Jularr.Backend.Data.Admin.Translation.V1
{
    public sealed class LocalTranslationModelsParametersV1 : BaseParameters
    {
    }

    public sealed class LocalTranslationModelParametersV1(string model) : BaseParameters
    {
        [DataField(typeof(TranslationFields), nameof(TranslationFields.Name))]
        public string Model { get; } = model;
    }

    // A model downloaded onto one machine of the order.
    public sealed class LocalTranslationPullParametersV1(string model, int machine) : BaseParameters
    {
        [DataField(typeof(TranslationFields), nameof(TranslationFields.Name))]
        public string Model { get; } = model;

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Machine))]
        public int Machine { get; } = machine;
    }

    // The Ollama machines in the Owner's order; empty means the configured endpoint alone.
    public sealed class TranslationMachinesV1(string[] machines) : BaseResponse
    {
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.TranslationMachines))]
        public string[] Machines { get; } = machines;
    }

    public sealed class TranslationMachinesDataV1(string[] machines) : BaseData
    {
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.TranslationMachines))]
        public string[] Machines { get; } = machines;
    }

    // One machine of the order as it answered just now: its address, whether it answered, and its installed and loaded models.
    public sealed class LocalTranslationMachineV1(string address, bool reachable, string[] installed, string[] running)
    {
        public string Address { get; } = address;
        public bool Reachable { get; } = reachable;
        public string[] Installed { get; } = installed;
        public string[] Running { get; } = running;
    }

    // Installed and Running are those of the machine that translates now (the first in the order that answers); Machines lists every one.
    public sealed class LocalTranslationModelsV1(bool configured, string[] approved, string[] installed, string[] running, string? problem, long verifiedFreeBytes, string downloadModel, string downloadState, int downloadProgress,
        LocalTranslationMachineV1[] machines, string downloadMachine) : BaseResponse
    {
        public LocalTranslationMachineV1[] Machines { get; } = machines;
        public string DownloadMachine { get; } = downloadMachine;
        public bool Configured { get; } = configured;
        public string[] Approved { get; } = approved;
        public string[] Installed { get; } = installed;
        public string[] Running { get; } = running;
        public string? Problem { get; } = problem;
        public long VerifiedFreeBytes { get; } = verifiedFreeBytes;
        public string DownloadModel { get; } = downloadModel;
        public string DownloadState { get; } = downloadState;
        public int DownloadProgress { get; } = downloadProgress;
    }

    public sealed class LocalTranslationBenchmarkV1(string model, string[] samples, double[] seconds, double[] tokensPerSecond) : BaseResponse
    {
        public string Model { get; } = model;
        public string[] Samples { get; } = samples;
        public double[] Seconds { get; } = seconds;
        public double[] TokensPerSecond { get; } = tokensPerSecond;
    }
}
