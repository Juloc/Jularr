using System.Text.Json;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Setup.Instance
{
    public sealed class InstanceModel(ReadInstanceSettingsV1 readSettings, ReadSetupModulesV1 readModules, ReadSetupProgressV1 readProgress,
        SaveSetupModulesV1 save, IAntiforgery antiforgery) : JularrPage
    {
        private InstanceSettingsDetailsV1? _settings;
        private SetupProgressV1? _progress;
        private IReadOnlyList<SetupModuleV1> _modules = [];
        private SaveSetupModulesDataV1? _input;

        protected override void Define(Page page)
        {
            page.Title("setup.step.Instance");
            page.Asset("Pages/Setup/Instance/Instance.entry.ts");
            page.Asset("Pages/Setup/Instance/Instance.entry.scss");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            page.Content.Add(SetupWizard.Frame(SetupStepType.Instance));
            if (_settings is not { } settings || _progress is not { } progress) return;

            var input = _input ?? new SaveSetupModulesDataV1(settings.Name, settings.Version, progress.Version,
                new BaseDataList<SetupModuleInputV1>(_modules.Select(module => new SetupModuleInputV1(module.Id, module.IsEnabled, module.Version))));
            var presets = new[] { "complete", "mediaServer", "requests" }.Select(preset => new Button(new Text("setup.preset." + preset)).Attribute("data-setup-preset", preset)).ToArray<IUiComponent>();
            var choices = new Div(presets).Class("setup-modules__presets").Attribute("hidden", "hidden").Attribute("role", "group").TextAttribute("aria-label", "setup.presets");
            var keys = _modules.ToDictionary(module => module.Id, module => module.Key);
            var form = new Form<SaveSetupModulesDataV1>(fields =>
            {
                fields.Hidden(item => item.Version);
                fields.Hidden(item => item.SetupVersion);
                fields.Text(item => item.Name, "admin.instance.name");
                foreach (var group in new[] { "media", "requests", "personal", "operations" })
                {
                    fields.Section("setup.modules." + group, section => section.Rows(item => item.Modules, (row, module) =>
                    {
                        row.Hidden(item => item.Id);
                        row.Hidden(item => item.Version);
                        row.Toggle(item => item.IsEnabled, "instance.module." + keys[module.Id], new FieldHelp(Info: ""));
                    }, module => keys.TryGetValue(module.Id, out var key) && Group(key) == group));
                }

                fields.Submit("setup.next");
            }, "/Setup/Instance", csrf, input, FormError);
            page.Content.Add(new Div(choices, form).Class("setup-modules").Attribute("data-setup-presets", JsonSerializer.Serialize(new Dictionary<string, long[]>
            {
                ["complete"] = [.. _modules.Where(module => module.Complete).Select(module => module.Id)],
                ["mediaServer"] = [.. _modules.Where(module => module.MediaServer).Select(module => module.Id)],
                ["requests"] = [.. _modules.Where(module => module.Requests).Select(module => module.Id)]
            })));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            if (_progress?.IsCompleted is true) return LocalRedirect("/");
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? name, long version, long setupVersion, List<ModuleRow> modules)
        {
            _input = new SaveSetupModulesDataV1(name ?? "", version, setupVersion,
                new BaseDataList<SetupModuleInputV1>(modules.Select(module => new SetupModuleInputV1(module.Id, module.IsEnabled, module.Version))));
            await save.ExecuteAsync<UpdateResult>(new SetupProgressParametersV1(), _input, HttpContext.RequestAborted);
            return LocalRedirect("/Setup/Storage");
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _progress = await readProgress.ExecuteAsync<SetupProgressV1>(cancellationToken: ct);
            _settings = await readSettings.ExecuteAsync<InstanceSettingsDetailsV1>(new ReadInstanceSettingsParametersV1(), ct);
            _modules = await readModules.ExecuteAsync<BaseDataList<SetupModuleV1>>(cancellationToken: ct);
        }

        private static string Group(string key) => key switch
        {
            "Library" or "Playback" or "Reading" or "Emulation" or "Subtitles" or "Trickplay" or "Translation" or "Ocr" or "SmartPdf" or "DiscoverCache" or "LocalDiscovery" => "media",
            "Requests" or "Acquisition" or "Uploads" => "requests",
            "Profiles" or "Devices" or "ProgressSync" or "HomeLayout" or "Collections" or "Calendar" or "Notifications" or "Statistics" or "UnitGroups" or "Franchises" or "Registration" => "personal",
            _ => "operations"
        };

        public sealed record ModuleRow(long Id, bool IsEnabled, long Version);
    }
}
