using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Browser.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Browser;
using Jularr.Integrations.Logic.Browser;

namespace Jularr.Backend.Service.Admin.Browser.V1
{
    // The browser of the instance: what it is, what it did and what an administrator can do with it.
    [ServiceEndpoint("GET", "/api/v1/admin/browser")]
    public sealed class ReadBrowserStatusV1 : AdminService<BrowserParametersV1>
    {
        private readonly ISourceBrowser _browser;
        private readonly BrowserPackages _packages;

        internal ReadBrowserStatusV1(ISourceBrowser browser, BrowserPackages packages)
        {
            _browser = browser;
            _packages = packages;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BrowserStatusV1>()];
        protected override ModuleRequirement GetInstanceModules(BrowserParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(BrowserParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override Task<IServiceOutput> OnRead()
        {
            var status = _browser.Status;
            return Task.FromResult<IServiceOutput>(new BrowserStatusV1(status.Path, status.RemoteAddress, status.ProfilePath, _browser.IsAvailable, status.IsRunning, status.Version,
                status.LastUrl, status.LastOutcome, status.LastError, status.LastAttemptAt, new PageResult<BrowserEventV1>(Events(), 1, 50), _packages.State, _packages.Error, _packages.DownloadedBytes, _packages.TotalBytes));
        }

        private BrowserEventV1[] Events() => [.. _browser.Events.Select(item => new BrowserEventV1(item.At, item.Kind, item.Message))];
    }

    // The browser starts, stops, begins again as a new client or reads one page.
    [ServiceEndpoint("POST", "/api/v1/admin/browser")]
    public sealed class RunBrowserActionV1 : AdminService<BrowserParametersV1, RunBrowserActionDataV1>
    {
        private readonly ISourceBrowser _browser;
        private readonly BrowserPackages _packages;

        internal RunBrowserActionV1(ISourceBrowser browser, BrowserPackages packages)
        {
            _browser = browser;
            _packages = packages;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BrowserStatusV1>()];
        protected override ModuleRequirement GetInstanceModules(BrowserParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(BrowserParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // The browser answers within its own limits; the page never waits longer than that.
            var action = ActionAsync();
            var finished = await Task.WhenAny(action, Task.Delay(TimeSpan.FromMinutes(2), CancellationToken));
            if (finished != action)
            {
                var status = _browser.Status;
                return new BrowserStatusV1(status.Path, status.RemoteAddress, status.ProfilePath, _browser.IsAvailable, status.IsRunning, status.Version,
                    status.LastUrl, status.LastOutcome, status.LastError, status.LastAttemptAt, new PageResult<BrowserEventV1>(Events(), 1, 50), _packages.State, _packages.Error, _packages.DownloadedBytes, _packages.TotalBytes);
            }

            await action;
            var state = _browser.Status;
            return new BrowserStatusV1(state.Path, state.RemoteAddress, state.ProfilePath, _browser.IsAvailable, state.IsRunning, state.Version,
                state.LastUrl, state.LastOutcome, state.LastError, state.LastAttemptAt, new PageResult<BrowserEventV1>(Events(), 1, 50), _packages.State, _packages.Error, _packages.DownloadedBytes, _packages.TotalBytes);
        }

        private async Task ActionAsync()
        {
            switch (Data.Action)
            {
                case "start":
                    await _browser.StartAsync(CancellationToken);
                    break;
                case "stop":
                    await _browser.StopAsync();
                    break;
                case "reset":
                    await _browser.ResetAsync();
                    break;
                case "read":
                    if (!Uri.TryCreate(Data.Url, UriKind.Absolute, out var url))
                    {
                        throw new DataValidationException(nameof(Data.Url), "invalid_url");
                    }

                    await _browser.ReadPageAsync(url, CancellationToken);
                    break;
                default:
                    throw new DataValidationException(nameof(Data.Action), "unknown_catalog_value");
            }
        }

        private BrowserEventV1[] Events() => [.. _browser.Events.Select(item => new BrowserEventV1(item.At, item.Kind, item.Message))];
    }
}
