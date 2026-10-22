using Jularr.Backend.Data.Common;
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

    public sealed class LocalTranslationModelsV1(bool configured, string[] approved, string[] installed, string[] running, string? problem) : BaseResponse
    {
        public bool Configured { get; } = configured;
        public string[] Approved { get; } = approved;
        public string[] Installed { get; } = installed;
        public string[] Running { get; } = running;
        public string? Problem { get; } = problem;
    }

    public sealed class LocalTranslationBenchmarkV1(string model, string[] samples, double[] seconds, double[] tokensPerSecond) : BaseResponse
    {
        public string Model { get; } = model;
        public string[] Samples { get; } = samples;
        public double[] Seconds { get; } = seconds;
        public double[] TokensPerSecond { get; } = tokensPerSecond;
    }
}
