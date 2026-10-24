using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Browser.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Logic.Instance;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Browser;

namespace Jularr.Backend.Service.System.Browser.V1
{
    // The worker asks the module owner what should run; the installer never reads the database.
    public sealed class ReconcileBrowserModuleV1 : SystemService<BrowserParametersV1, RunBrowserActionDataV1>
    {
        private readonly ManagedBrowserModule _browser;

        internal ReconcileBrowserModuleV1(ManagedBrowserModule browser) => _browser = browser;
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(BrowserParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(BrowserParametersV1 parameters, Type resultType) => new("system.browser");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var enabled = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var modules = await ctx.ReadModuleStatesAsync(new InstanceModuleParameters(["Browser"]), ct);
                return modules.Any(module => module.Key == "Browser" && module.IsEnabled);
            }, CancellationToken);

            await _browser.ReconcileAsync(enabled, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
