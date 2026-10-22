using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Admin.Request.V1;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Backend.Service.Admin.Library.V1;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Backend.Service.Admin.Request.V1;
using Jularr.Backend.Service.Admin.Storage.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Setup.Overview
{
    // The last step: what was chosen in every step, a way back to each of them, what still blocks and Finish.
    public sealed class OverviewModel(ReadSetupProgressV1 readProgress, CompleteSetupV1 complete, ReadStorageMountsV1 readMounts, ReadLibraryRootsV1 readRoots, ReadInstanceMediaV1 readMedia,
        ReadSetupModulesV1 readModules, ReadAcquisitionConnectionsV1 readConnections, ReadAcquisitionProfilesV1 readProfiles, ReadProvidersV1 readProviders, ReadRequestRulesV1 readRules,
        FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private SetupProgressV1? _progress;
        private IReadOnlyList<StorageMountDetailsV1> _mounts = [];
        private IReadOnlyList<LibraryRootDetailsV1> _roots = [];
        private IReadOnlyList<InstanceMediaDetailsV1> _media = [];
        private IReadOnlyList<SetupModuleV1> _modules = [];
        private IReadOnlyList<AcquisitionConnectionDetailsV1> _connections = [];
        private IReadOnlyList<AcquisitionProfileDetailsV1> _profiles = [];
        private IReadOnlyList<ProviderDetailsV1> _providers = [];
        private RequestRuleDetailsV1? _rule;

        protected override void Define(Page page)
        {
            page.Title("setup.step.Overview");
            if (_progress is not { } progress) return;
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var places = SetupWizard.Active(progress, texts).Where(place => place.Step is not null && place.Key != "Overview").ToList();

            // What still needs a look, by the step that fixes it.
            var attention = progress.Blockers.Select(key => (Key: key, Blocks: true)).Concat(progress.Warnings.Select(key => (Key: key, Blocks: false)))
                .Where(item => StepOf(item.Key) is not null).ToLookup(item => StepOf(item.Key)!);
            var rows = places.Select(place =>
            {
                var issues = attention[place.Key].ToArray();
                var chip = issues.Length == 0 ? new StateChip(new Text("setup.overview.ready"), UiTone.Success, new Icon("check", assets, 14))
                    : new StateChip(new Text(issues.Any(item => item.Blocks) ? "setup.overview.needed" : "setup.overview.optional"), issues.Any(item => item.Blocks) ? UiTone.Danger : UiTone.Warning, new Icon("alert", assets, 14));
                return (IUiComponent)new Div(new IconTile(new Icon(Icon(place.Key), assets, 18), UiTone.Accent), new Div(new Strong(new Truncated(texts[place.LabelKey])), new Span(new Truncated(Value(place.Key, progress))).Class("setup-overview__value")).Class("setup-overview__text"),
                    chip, new A(new Icon("pencil", assets, 14), new Text("common.edit")).Class("button button--compact button--soft").Attribute("href", place.Path)).Class("setup-overview__row");
            }).ToArray();
            page.Content.Add(new Div(new Div(rows).Class("setup-overview__list")).Class("ui-section setup-overview"));

            var finish = new Button(new Icon("check", assets, 15), new Text("setup.finish")).Primary();
            if (progress.Blockers.Length > 0) finish.Disabled();
            var items = progress.Blockers.Concat(progress.Warnings).Select(Check).ToArray();
            page.Content.Add(new Div(new Div(items).Class("setup-overview__checks"),
                new PostForm("/Setup/Overview?handler=Finish", csrf, [("version", progress.Version.ToString(CultureInfo.InvariantCulture))], finish.Submit())).Class("setup-foot setup-foot--finish"));
            if (FormError is not null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
        }

        // A blocking or optional check, named with the step that fixes it.
        private IUiComponent Check(string key)
        {
            var place = StepOf(key);
            var text = key.StartsWith("library.", StringComparison.Ordinal) ? new Span(new Text("media." + key["library.".Length..]), new ValueText(": "), new Text("setup.check.library")) : new Span(new Text("setup.check." + key));
            return place is null ? new P(text) : new P(text, new ValueText(" "), new A(new Text("common.edit")).Attribute("href", Path(place)));
        }

        private static string Path(string place) => "/Setup/" + (place == "Modules" ? "Instance" : place);

        // The step a check belongs to.
        private static string? StepOf(string key) => key switch
        {
            _ when key.StartsWith("library.", StringComparison.Ordinal) => "Libraries",
            "tmdb" => "Providers",
            "indexer" => "Indexers",
            "downloader" => "Downloads",
            _ when key.StartsWith("downloadStorage.", StringComparison.Ordinal) => "DownloadStorage",
            _ => null
        };

        private static string Icon(string place) => place switch
        {
            "Begin" => "spark",
            "Storage" => "drive",
            "Takeover" => "swap",
            "Modules" => "layers",
            "Profiles" => "sliders",
            "Libraries" => "library",
            "Requests" => "requests",
            "DownloadStorage" => "download",
            "Downloads" => "download",
            "Indexers" => "search",
            _ => "providers"
        };

        // The facts of a step in one line.
        private string Value(string place, SetupProgressV1 progress)
        {
            string Count(string key, int count) => string.Format(CultureInfo.CurrentCulture, texts[key], count);
            var clients = _connections.Count(item => item.AcquisitionConnectionTypeId is not AcquisitionConnectionType.Newznab);
            return place switch
            {
                "Begin" => texts[progress.SetupStartTypeId == SetupStartType.Takeover ? "setup.begin.takeover.short" : "setup.begin.new"],
                "Storage" => Count("setup.overview.connections", _mounts.Count),
                "Takeover" => string.Join(" · ", [Count("setup.overview.profiles", _profiles.Count), Count("setup.overview.clients", clients), Count("setup.overview.indexers", _connections.Count - clients)]),
                "Modules" => string.Join(" · ", [Count("setup.overview.modules", _modules.Count(module => module.IsEnabled)), Count("setup.overview.types", _media.Count(item => item.IsEnabled))]),
                "Profiles" => Count("setup.overview.profiles", _profiles.Count),
                "Libraries" => _roots.Count == 0 ? texts["setup.libraries.off"] : string.Join(", ", _roots.Select(root => root.Name).Distinct().Take(6)),
                "Requests" => _rule is null ? "" : texts[_rule.IsAutomaticApproval ? "request.rule.automatic" : "request.rule.manual"] + " · " + (_rule.RequestLimit is { } limit ? string.Format(CultureInfo.CurrentCulture, texts["request.rule.summary"], limit, _rule.PeriodDays) : texts["request.rule.unlimited"]),
                "DownloadStorage" => progress.Blockers.Count(key => key.StartsWith("downloadStorage.", StringComparison.Ordinal)) is var open and > 0 ? Count("setup.overview.areas", open) : texts["setup.overview.set"],
                "Downloads" => Count("setup.overview.clients", clients),
                "Indexers" => Count("setup.overview.indexers", _connections.Count - clients),
                _ => Count("setup.overview.providers", _providers.Count(provider => provider.IsEnabled))
            };
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostFinishAsync([BindRequired] long version)
        {
            await complete.ExecuteAsync<UpdateResult>(new SetupProgressParametersV1(), new CompleteSetupDataV1(version), HttpContext.RequestAborted);
            return LocalRedirect("/");
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _progress = await readProgress.ExecuteAsync<SetupProgressV1>(new SetupProgressParametersV1(), ct);
            _mounts = (await readMounts.ExecuteAsync<PageResult<StorageMountDetailsV1>>(new ReadStorageMountsParametersV1(1), ct)).Items;
            _roots = (await readRoots.ExecuteAsync<PageResult<LibraryRootDetailsV1>>(new ReadLibraryRootsParametersV1(null, 1, 100), ct)).Items;
            _media = await readMedia.ExecuteAsync<BaseDataList<InstanceMediaDetailsV1>>(new ReadInstanceMediaParametersV1(), ct);
            _modules = await readModules.ExecuteAsync<BaseDataList<SetupModuleV1>>(cancellationToken: ct);
            _providers = (await readProviders.ExecuteAsync<PageResult<ProviderDetailsV1>>(new ReadProvidersParametersV1(1, 100), ct)).Items;
            if (texts.Can(null, "Acquisition"))
            {
                _connections = (await readConnections.ExecuteAsync<PageResult<AcquisitionConnectionDetailsV1>>(new ReadAcquisitionConnectionsParametersV1(1, 100), ct)).Items;
                _profiles = await readProfiles.ExecuteAsync<BaseDataList<AcquisitionProfileDetailsV1>>(new ReadAcquisitionProfilesParametersV1(), ct);
            }

            if (texts.Can(null, "Requests")) _rule = (await readRules.ExecuteAsync<BaseDataList<RequestRuleDetailsV1>>(new ReadRequestRulesParametersV1(), ct)).FirstOrDefault(rule => rule.IsDefault);
        }
    }
}
