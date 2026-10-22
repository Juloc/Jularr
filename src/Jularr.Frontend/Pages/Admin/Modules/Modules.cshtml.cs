using System.Text.Json;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Modules
{
    // Every module as a card with its switch; the switches are one form, saved together by the page's pill, so a module and what it needs change in one step.
    public sealed class ModulesModel(ReadInstanceModulesV1 read, UpdateInstanceModulesV1 update, FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string Address = "/Admin/Instance/Modules";

        // The icon of what a module does, from the project's icon set.
        private static readonly Dictionary<string, string> Icons = new()
        {
            ["Library"] = "library",
            ["Requests"] = "requests",
            ["Acquisition"] = "download",
            ["Playback"] = "play",
            ["Reading"] = "book",
            ["Profiles"] = "users",
            ["Emulation"] = "gamepad",
            ["Notifications"] = "bell",
            ["Collections"] = "bookmark",
            ["Calendar"] = "calendar",
            ["Statistics"] = "pulse",
            ["UnitGroups"] = "list",
            ["HomeLayout"] = "home",
            ["Trickplay"] = "scan",
            ["Franchises"] = "layers",
            ["Diagnostics"] = "activity",
            ["Subtitles"] = "subtitles",
            ["Uploads"] = "upload",
            ["ProgressSync"] = "sync",
            ["AniList"] = "anilist",
            ["Devices"] = "devices",
            ["Registration"] = "user-plus",
            ["Translation"] = "globe",
            ["Ocr"] = "text-select",
            ["SmartPdf"] = "document",
            ["DiscoverCache"] = "drive",
            ["LocalDiscovery"] = "network"
        };

        private IReadOnlyList<InstanceModuleDetailsV1> _modules = [];
        private UpdateInstanceModulesDataV1? _input;

        protected override void Define(Page page)
        {
            page.Title("admin.instance.modules");
            page.Navigation("nav.admin");
            page.Asset("Framework/Components/SettingCard.entry.scss");
            page.Asset("Framework/Components/Truncated.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            page.Asset("Pages/Admin/Modules/Modules.entry.ts");
            if (_modules.Count == 0) return;

            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var input = _input ?? new UpdateInstanceModulesDataV1(new BaseDataList<InstanceModuleInputV1>(_modules.Select(module => new InstanceModuleInputV1(module.Id, module.IsEnabled, module.Version))));
            var byId = _modules.ToDictionary(module => module.Id);
            var enabled = input.Modules.ToDictionary(module => byId[module.Id].Key, module => module.IsEnabled);
            var form = new Form<UpdateInstanceModulesDataV1>(fields =>
            {
                fields.Rows(item => item.Modules, (row, item) =>
                {
                    var module = byId[item.Id];
                    row.Hidden(data => data.Id);
                    row.Hidden(data => data.Version);
                    row.Switch(data => data.IsEnabled, "instance.module." + module.Key, control => Card(module, control, item.IsEnabled, enabled));
                });
                fields.Save();
            }, Address, csrf, input, FormError);
            page.Content.Add(new Div(form).Class("setting-cards"));

            // What belongs to a module but is not a switch: the speech models of the reader's read-aloud.
            page.Content.Add(new ActionDock(assets, "admin.modules.actions", [new A(new Icon("headphones", assets, 16), new Span(new Text("speech.models.title")).Class("action-dock__text"))
                .Class("button").Attribute("href", "/Admin/Instance/Speech")]));
        }

        // The card of one module: its name and state, its switch, and the modules it needs and the ones that need it.
        private IUiComponent Card(InstanceModuleDetailsV1 module, IUiComponent control, bool isOn, IReadOnlyDictionary<string, bool> enabled)
        {
            var tags = new List<IUiComponent>();
            if (module.Requires.Length > 0)
            {
                tags.Add(new Span(new Text("settings.module.needs")).Class("setting-card__tags-label"));
                tags.AddRange(module.Requires.Select(key => (IUiComponent)new Tag(new Truncated(texts["instance.module." + key])).Attribute("data-needs", key)
                    .Class(enabled.GetValueOrDefault(key) ? "tag" : "tag tag--warning")));
            }

            // Those that need this module and are on, in one line; the script keeps it as they are switched.
            var dependants = _modules.Where(other => other.Requires.Contains(module.Key)).ToList();
            if (dependants.Count > 0)
            {
                var names = string.Join(", ", dependants.Where(other => enabled.GetValueOrDefault(other.Key)).Select(other => texts["instance.module." + other.Key]));
                var needed = new Tag(new Span(new Text("settings.module.neededBy")).Class("setting-card__tags-label"), new Truncated(names)).Attribute("data-needed-names",
                    JsonSerializer.Serialize(dependants.ToDictionary(other => other.Key, other => texts["instance.module." + other.Key])));
                tags.Add(names.Length > 0 ? needed : needed.Attribute("hidden", "hidden"));
            }

            var blocked = isOn ? dependants.Any(other => enabled.GetValueOrDefault(other.Key)) : module.Requires.Any(key => !enabled.GetValueOrDefault(key));
            var card = new SettingCard(new IconTile(new Icon(Icons.GetValueOrDefault(module.Key, "settings"), assets, 17)), new Truncated(texts["instance.module." + module.Key]), control, isOn,
                "admin.providers.enabled", "admin.providers.disabled", tags).Data("module", module.Key).Data("requires", string.Join(' ', module.Requires));
            return blocked ? card.Blocked() : card;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        // Every module comes back as shown, so one save is one consistent step.
        public async Task<IActionResult> OnPostAsync(List<ModuleRow> modules)
        {
            _input = new UpdateInstanceModulesDataV1(new BaseDataList<InstanceModuleInputV1>(modules.Select(module => new InstanceModuleInputV1(module.Id, module.IsEnabled, module.Version))));
            await update.ExecuteAsync<UpdateResult>(new UpdateInstanceModulesParametersV1(), _input, HttpContext.RequestAborted);
            return LocalRedirect(Address);
        }

        protected override async Task LoadDataAsync()
        {
            _modules = await read.ExecuteAsync<BaseDataList<InstanceModuleDetailsV1>>(new ReadInstanceModulesParametersV1(), HttpContext.RequestAborted);
        }

        public sealed record ModuleRow(long Id, bool IsEnabled, long Version);
    }
}
