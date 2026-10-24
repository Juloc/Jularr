using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Data.System.Browser.V1;
using Jularr.Backend.Logic.Instance;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Browser;
using Jularr.Integrations.Logic.Browser;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.System.Browser.V1
{
    // Reconcile the Owner's module switch with the managed browser files; never install system packages.
    public sealed class ReconcileBrowserModuleV1 : SystemService<BrowserCycleParametersV1, NoData>
    {
        private readonly BrowserPackages _packages;
        private readonly ISourceBrowser _browser;
        private readonly BrowserOptions _options;

        internal ReconcileBrowserModuleV1(BrowserPackages packages, ISourceBrowser browser, IOptions<BrowserOptions> options)
        {
            _packages = packages;
            _browser = browser;
            _options = options.Value;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ServicePermission GetPermission(BrowserCycleParametersV1 parameters, Type resultType) => new("system.acquisition");
        protected override ModuleRequirement GetInstanceModules(BrowserCycleParametersV1 parameters) => ModuleRequirement.None;

        protected override async Task<IServiceOutput> OnExecute()
        {
            var modules = await Context.ExecuteBatchAsync((ctx, ct) =>
                ctx.ReadModuleStatesAsync(new InstanceModuleParameters(["Browser"]), ct), CancellationToken);
            var enabled = modules.Any(module => module.Key == "Browser" && module.IsEnabled);
            if (!enabled)
            {
                if (_packages.State != "disabled" || _packages.Enabled || _packages.HasManagedData)
                {
                    _packages.Enabled = false;
                    await _browser.StopAsync();
                    await _packages.RemoveAsync(CancellationToken);
                }

                return new UpdateResult(false);
            }

            if (_options.Path is null && string.IsNullOrWhiteSpace(_options.RemoteAddress))
            {
                if (!_packages.CanRetryInstall) return new UpdateResult(false);
                await _packages.InstallAsync(CancellationToken);
            }

            _packages.Enabled = true;
            if (!_browser.Status.IsRunning) await _browser.StartAsync(CancellationToken);
            return new UpdateResult(true);
        }
    }
}
