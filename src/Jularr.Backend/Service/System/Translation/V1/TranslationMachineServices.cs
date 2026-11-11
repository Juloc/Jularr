using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.System.Translation.V1;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Translation.V1
{
    // The translationMachines job: every translation provider asks its machines how they are, outside any transaction, so the translation page, the Reader
    // and the choices of the translation jobs read their state instead of asking them. A machine that does not answer is a state, not a failure of the job.
    public sealed class RefreshTranslationMachinesV1 : SystemService<TranslationMachinesParametersV1, NoData>
    {
        private readonly TranslationProviders _providers;

        internal RefreshTranslationMachinesV1(TranslationProviders providers)
        {
            _providers = providers;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(TranslationMachinesParametersV1 parameters) => ModuleRequirement.All("Translation");
        protected override ServicePermission GetPermission(TranslationMachinesParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var setup = await Context.ExecuteBatchAsync(TranslationProviders.SetupAsync, CancellationToken);
            await _providers.CheckAsync(setup, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
