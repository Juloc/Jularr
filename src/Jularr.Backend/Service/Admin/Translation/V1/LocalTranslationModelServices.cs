using System.Net.Http;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Translation;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Admin.Translation.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/translation/models")]
    public sealed class ReadLocalTranslationModelsV1 : AdminService<LocalTranslationModelsParametersV1>
    {
        private readonly OllamaLocalModels _models;
        private readonly LocalTranslationOptions _options;

        internal ReadLocalTranslationModelsV1(OllamaLocalModels models, IOptions<LocalTranslationOptions> options)
        {
            _models = models;
            _options = options.Value;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LocalTranslationModelsV1>()];
        protected override ModuleRequirement GetInstanceModules(LocalTranslationModelsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(LocalTranslationModelsParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var server = OllamaLocalModels.Endpoint(_options.Endpoint);
            if (server is null) return new LocalTranslationModelsV1(false, OllamaLocalModels.Candidates, [], [], "Configure a local Ollama endpoint.");
            try
            {
                return new LocalTranslationModelsV1(true, OllamaLocalModels.Candidates,
                    await _models.InstalledAsync(server, CancellationToken), await _models.RunningAsync(server, CancellationToken), null);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
            {
                return new LocalTranslationModelsV1(true, OllamaLocalModels.Candidates, [], [], "Ollama is not responding correctly.");
            }
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/translation/models/{model}/pull")]
    public sealed class PullLocalTranslationModelV1 : AdminService<LocalTranslationModelParametersV1, NoData>
    {
        private readonly OllamaLocalModels _models;
        private readonly LocalTranslationOptions _options;
        private readonly LocalTranslationGate _gate;

        internal PullLocalTranslationModelV1(OllamaLocalModels models, IOptions<LocalTranslationOptions> options, LocalTranslationGate gate)
        {
            _models = models;
            _options = options.Value;
            _gate = gate;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(LocalTranslationModelParametersV1 parameters) => ModuleRequirement.All("Translation");
        protected override ServicePermission GetPermission(LocalTranslationModelParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var server = OllamaLocalModels.Endpoint(_options.Endpoint) ?? throw new ServiceUnavailableException("translation_local_unavailable");
            if (!OllamaLocalModels.Supported(Parameters.Model)) throw new DataValidationException(nameof(Parameters.Model), "unknown_model");
            if (!await _gate.HeavyWork.WaitAsync(TimeSpan.Zero, CancellationToken)) throw new ServiceUnavailableException("translation_local_busy");
            try
            {
                await _models.PullAsync(server, Parameters.Model, CancellationToken);
                return new UpdateResult(true);
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException)
            {
                throw new ServiceUnavailableException("translation_local_unavailable", exception);
            }
            finally
            {
                _gate.HeavyWork.Release();
            }
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/translation/models/{model}/benchmark")]
    public sealed class BenchmarkLocalTranslationModelV1 : AdminService<LocalTranslationModelParametersV1, NoData>
    {
        private readonly OllamaLocalModels _models;
        private readonly LocalTranslationOptions _options;
        private readonly LocalTranslationGate _gate;

        internal BenchmarkLocalTranslationModelV1(OllamaLocalModels models, IOptions<LocalTranslationOptions> options, LocalTranslationGate gate)
        {
            _models = models;
            _options = options.Value;
            _gate = gate;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LocalTranslationBenchmarkV1>()];
        protected override ModuleRequirement GetInstanceModules(LocalTranslationModelParametersV1 parameters) => ModuleRequirement.All("Translation");
        protected override ServicePermission GetPermission(LocalTranslationModelParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var server = OllamaLocalModels.Endpoint(_options.Endpoint) ?? throw new ServiceUnavailableException("translation_local_unavailable");
            if (!OllamaLocalModels.Supported(Parameters.Model)) throw new DataValidationException(nameof(Parameters.Model), "unknown_model");
            if (!(await _models.InstalledAsync(server, CancellationToken)).Contains(Parameters.Model, StringComparer.Ordinal))
                throw new ServiceUnavailableException("translation_local_not_installed");
            if (!await _gate.HeavyWork.WaitAsync(TimeSpan.Zero, CancellationToken)) throw new ServiceUnavailableException("translation_local_busy");
            try
            {
                var samples = new (string Source, string Target, string Text)[]
                {
                    ("id", "de", "Rina pulang ke rumah. Dia membawa sebuah buku, tetapi hujan semakin deras."),
                    ("ja", "de", "雨が強くなった。美咲は窓を閉めて、本を机の上に置いた。"),
                    ("de", "id", "Die Tür war offen, doch niemand wagte es, das dunkle Zimmer zu betreten.")
                };
                var translated = new List<string>();
                var seconds = new List<double>();
                var speed = new List<double>();
                foreach (var sample in samples)
                {
                    var result = await _models.TranslateAsync(server, Parameters.Model, sample.Source, sample.Target, sample.Text, CancellationToken);
                    translated.Add(sample.Source + " → " + sample.Target + ": " + result.Text);
                    seconds.Add(result.Seconds);
                    speed.Add(result.TokensPerSecond);
                }
                return new LocalTranslationBenchmarkV1(Parameters.Model, [.. translated], [.. seconds], [.. speed]);
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException)
            {
                throw new ServiceUnavailableException("translation_local_unavailable", exception);
            }
            finally
            {
                _gate.HeavyWork.Release();
            }
        }
    }
}
