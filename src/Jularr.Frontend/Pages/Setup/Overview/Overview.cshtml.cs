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

            // What each step lacks, by the step that fixes it; whatever concerns the whole instance has no step and is listed under the rows.
            var checks = progress.Blockers.Select(key => (Key: key, Blocks: true)).Concat(progress.Warnings.Select(key => (Key: key, Blocks: false))).ToList();
            var attention = checks.Where(item => SetupWizard.StepOf(item.Key) is not null).ToLookup(item => SetupWizard.StepOf(item.Key)!);
            var rows = places.Select(place =>
            {
                var issues = attention[place.Key].ToArray();
                var (state, tone, icon) = State(issues);
                var line = issues.Length == 0 ? Value(place.Key, progress) : string.Join(" · ", issues.Select(item => Reason(item.Key)));
                return (IUiComponent)new Div(new IconTile(new Icon(Icon(place.Key), assets, 18), UiTone.Accent), new Div(new Strong(new Truncated(texts[place.LabelKey])), new Span(new Truncated(line)).Class("setup-overview__value")).Class("setup-overview__text"),
                    new StateChip(new Text(state), tone, new Icon(icon, assets, 14)), new A(new Icon("pencil", assets, 14), new Text("common.edit")).Class("button button--compact button--soft").Attribute("href", place.Path))
                    .Class("setup-overview__row").Attribute("data-state", state.Split('.')[^1]);
            }).ToArray();
            page.Content.Add(new Div(new Div(rows).Class("setup-overview__list")).Class("ui-section setup-overview"));

            // Finish stays possible; with something required missing it asks once more.
            var version = progress.Version.ToString(CultureInfo.InvariantCulture);
            var general = checks.Where(item => SetupWizard.StepOf(item.Key) is null).Select(item => (IUiComponent)new P(new Text("setup.check." + item.Key))).ToArray();
            var finish = progress.Blockers.Length == 0
                ? (IUiComponent)new PostForm("/Setup/Overview?handler=Finish", csrf, [("version", version)], new Button(new Icon("check", assets, 15), new Text("setup.finish")).Primary().Submit())
                : new Div(Modal.Opener(new Button(new Icon("alert", assets, 15), new Text("setup.finish.anyway")).Warn(), "finish-anyway"),
                    new Modal(assets, "finish-anyway", "setup.finish.confirm", [new Form<FinishSetupDataV1>(form =>
                    {
                        form.Hidden(item => item.Version);
                        form.Hidden(item => item.AcceptMissing);
                        form.Cancel().Submit(new Button(new Text("setup.finish.anyway")).Warn());
                    }, "/Setup/Overview?handler=Finish", csrf, new FinishSetupDataV1(progress.Version, true), null)]));
            page.Content.Add(new Div(new Div(general).Class("setup-overview__checks"), finish).Class("setup-foot setup-foot--finish"));
            if (FormError is not null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
        }

        // The state of a step from what it lacks: empty and required, empty and optional, or there but not working.
        private static (string Text, UiTone Tone, string Icon) State((string Key, bool Blocks)[] issues)
        {
            if (issues.Length == 0) return ("setup.overview.ready", UiTone.Success, "check");
            if (issues.Any(item => item.Blocks && !SetupWizard.IsAttention(item.Key))) return ("setup.overview.missing", UiTone.Danger, "alert");
            return issues.Any(item => !SetupWizard.IsAttention(item.Key)) ? ("setup.overview.skipped", UiTone.Warning, "minus") : ("setup.overview.attention", UiTone.Warning, "alert");
        }

        // The plain reason of a check.
        private string Reason(string key) => key.StartsWith("library.", StringComparison.Ordinal) && !key.EndsWith(".none", StringComparison.Ordinal)
            ? texts["media." + key["library.".Length..]] + ": " + texts["setup.check.library"]
            : texts["setup.check." + key];

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

        public async Task<IActionResult> OnPostFinishAsync([BindRequired] long version, bool acceptMissing)
        {
            await complete.ExecuteAsync<UpdateResult>(new SetupProgressParametersV1(), new FinishSetupDataV1(version, acceptMissing), HttpContext.RequestAborted);
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
