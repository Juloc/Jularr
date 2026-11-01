using System.Net.Http;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Translation;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Admin.Translation.V1
{
    // Every machine of the order as it answers now; the first that answers with a model is the one that translates.
    [ServiceEndpoint("GET", "/api/v1/admin/translation/models")]
    public sealed class ReadLocalTranslationModelsV1 : AdminService<LocalTranslationModelsParametersV1>
    {
        private readonly OllamaLocalModels _models;
        private readonly LocalTranslationOptions _options;
        private readonly LocalTranslationDownloads _downloads;
        private readonly LocalTranslationMachines _machines;

        internal ReadLocalTranslationModelsV1(OllamaLocalModels models, IOptions<LocalTranslationOptions> options, LocalTranslationDownloads downloads,
            LocalTranslationMachines machines)
        {
            _models = models;
            _options = options.Value;
            _downloads = downloads;
            _machines = machines;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LocalTranslationModelsV1>()];
        protected override ModuleRequirement GetInstanceModules(LocalTranslationModelsParametersV1 parameters) => ModuleRequirement.All("Translation");
        protected override ServicePermission GetPermission(LocalTranslationModelsParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var saved = await LocalTranslationMachines.SavedAsync(Context, CancellationToken);
            var freeBytes = await LocalTranslationMachines.FreeBytesAsync(Context, _options.OperatorVerifiedFreeBytes, CancellationToken);
            var order = _machines.Order(saved);
            var progress = _downloads.Current();
            if (order.Length == 0) return new LocalTranslationModelsV1(false, OllamaLocalModels.Candidates, [], [], "Configure a local Ollama endpoint.",
                freeBytes, progress.Model, progress.State, progress.Progress, [], progress.Machine);
            var machines = await Task.WhenAll(order.Select(StatusAsync));
            var active = machines.FirstOrDefault(machine => machine.Reachable && machine.Installed.Any(OllamaLocalModels.Supported))
                ?? machines.FirstOrDefault(machine => machine.Reachable);
            return new LocalTranslationModelsV1(true, OllamaLocalModels.Candidates, active?.Installed ?? [], active?.Running ?? [],
                active is null ? "Ollama is not responding correctly." : null,
                freeBytes, progress.Model, progress.State, progress.Progress, machines, progress.Machine);
        }

        private async Task<LocalTranslationMachineV1> StatusAsync(Uri server)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                return new LocalTranslationMachineV1(server.ToString(), true, await _models.InstalledAsync(server, timeout.Token), await _models.RunningAsync(server, timeout.Token));
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException
                || exception is OperationCanceledException && !CancellationToken.IsCancellationRequested)
            {
                return new LocalTranslationMachineV1(server.ToString(), false, [], []);
            }
        }
    }

    // Downloads a model onto one machine of the order, by its place.
    [ServiceEndpoint("POST", "/api/v1/admin/translation/machines/{machine:int}/models/{model}/pull")]
    public sealed class PullLocalTranslationModelV1 : AdminService<LocalTranslationPullParametersV1, NoData>
    {
        private readonly LocalTranslationOptions _options;
        private readonly LocalTranslationDownloads _downloads;
        private readonly LocalTranslationMachines _machines;

        internal PullLocalTranslationModelV1(IOptions<LocalTranslationOptions> options, LocalTranslationDownloads downloads, LocalTranslationMachines machines)
        {
            _options = options.Value;
            _downloads = downloads;
            _machines = machines;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(LocalTranslationPullParametersV1 parameters) => ModuleRequirement.All("Translation");
        protected override ServicePermission GetPermission(LocalTranslationPullParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var order = _machines.Order(await Context.ExecuteBatchAsync(LocalTranslationMachines.SavedAsync, CancellationToken));
            if (Parameters.Machine >= order.Length)
                throw new ServiceUnavailableException("translation_local_unavailable");
            if (!OllamaLocalModels.Supported(Parameters.Model))
                throw new DataValidationException(nameof(Parameters.Model), "unknown_model");
            var freeBytes = await Context.ExecuteBatchAsync((ctx, ct) => LocalTranslationMachines.FreeBytesAsync(ctx, _options.OperatorVerifiedFreeBytes, ct), CancellationToken);
            if (freeBytes == 0)
                throw new ServiceUnavailableException("translation_local_space_unverified");
            if (!_downloads.Queue(Parameters.Model, order[Parameters.Machine], freeBytes))
                throw new ServiceUnavailableException("translation_local_busy_or_capacity");
            return new UpdateResult(true);
        }
    }

    // The Owner's order of Ollama machines: private network addresses only, at most eight; empty leaves the configured endpoint alone.
    [ServiceEndpoint("PUT", "/api/v1/admin/translation/machines")]
    public sealed class SaveTranslationMachinesV1 : AdminService<LocalTranslationModelsParametersV1, TranslationMachinesDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(LocalTranslationModelsParametersV1 parameters) => ModuleRequirement.All("Translation");
        protected override ServicePermission GetPermission(LocalTranslationModelsParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var machines = new List<string>();
            foreach (var value in Data.Machines.Select(machine => machine.Trim()).Where(machine => machine.Length > 0))
            {
                // An address without a scheme or port is the usual Ollama one (http, port 11434), as Ollama's own settings show it.
                var typed = value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value;
                if (!Uri.TryCreate(typed, UriKind.Absolute, out var parsed) || parsed.AbsolutePath != "/" || parsed.Query.Length != 0)
                    throw new DataValidationException(nameof(Data.Machines), "translation_machine_invalid");
                var address = OllamaLocalModels.Endpoint(parsed.Scheme + "://" + parsed.Host + ":" + (parsed.IsDefaultPort ? 11434 : parsed.Port) + "/")
                    ?? throw new DataValidationException(nameof(Data.Machines), "translation_machine_invalid");
                if (!machines.Contains(address.ToString(), StringComparer.Ordinal)) machines.Add(address.ToString());
            }

            var sql = new SqlStatement();
            sql.Statement = """UPDATE "InstanceSettings" SET "TranslationMachines" = @Machines WHERE "Id" = 1;""";
            sql.Parameters.Add(new TranslationMachinesDataV1([.. machines]));
            await Context.ExecuteAsyncForAffectedRows(sql, CancellationToken);
            return new UpdateResult(true);
        }
    }

    // The free space of Ollama's model volume, which only the Owner can know: model downloads are offered up to it; 0 leaves the configured value.
    [ServiceEndpoint("PUT", "/api/v1/admin/translation/free-bytes")]
    public sealed class SaveTranslationFreeBytesV1 : AdminService<LocalTranslationModelsParametersV1, TranslationFreeBytesDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(LocalTranslationModelsParametersV1 parameters) => ModuleRequirement.All("Translation");
        protected override ServicePermission GetPermission(LocalTranslationModelsParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var sql = new SqlStatement();
            sql.Statement = """UPDATE "InstanceSettings" SET "TranslationFreeBytes" = @FreeBytes WHERE "Id" = 1;""";
            sql.Parameters.Add(Data);
            await Context.ExecuteAsyncForAffectedRows(sql, CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/translation/models/{model}/benchmark")]
    public sealed class BenchmarkLocalTranslationModelV1 : AdminService<LocalTranslationModelParametersV1, NoData>
    {
        private readonly OllamaLocalModels _models;
        private readonly LocalTranslationMachines _machines;
        private readonly LocalTranslationGate _gate;

        internal BenchmarkLocalTranslationModelV1(OllamaLocalModels models, LocalTranslationMachines machines, LocalTranslationGate gate)
        {
            _models = models;
            _machines = machines;
            _gate = gate;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LocalTranslationBenchmarkV1>()];
        protected override ModuleRequirement GetInstanceModules(LocalTranslationModelParametersV1 parameters) => ModuleRequirement.All("Translation");
        protected override ServicePermission GetPermission(LocalTranslationModelParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (!OllamaLocalModels.Supported(Parameters.Model)) throw new DataValidationException(nameof(Parameters.Model), "unknown_model");
            // The machine that translates now with this model.
            var saved = await Context.ExecuteBatchAsync(LocalTranslationMachines.SavedAsync, CancellationToken);
            var server = (await _machines.ChooseAsync(saved, CancellationToken, Parameters.Model))?.Server
                ?? throw new ServiceUnavailableException("translation_local_not_installed");
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
