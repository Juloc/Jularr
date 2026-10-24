using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Setup.Instance
{
    public sealed class InstanceModel(ReadInstanceSettingsV1 readSettings, ReadSetupModulesV1 readModules, ReadInstanceModulesV1 readDetails, ReadInstanceMediaV1 readMedia,
        ReadSetupProgressV1 readProgress, ReadWorksV1 readWorks, SaveSetupModulesV1 save, FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        // The basic functions, as tinted cards in a grid.
        private static readonly string[] Basics = ["Library", "Playback", "Reading", "Requests", "Acquisition"];

        // The choices that belong to a media type or subtype, under it.
        private static readonly Dictionary<string, string[]> Subs = new()
        {
            ["Series"] = ["Trickplay", "Subtitles"],
            ["Anime"] = ["AniList"],
            ["Book"] = ["Translation", "Ocr", "SmartPdf"],
            ["Manga"] = ["Uploads"],
            ["Game"] = ["Emulation"]
        };

        // A subtype switches on only below one of these types.
        private static readonly Dictionary<string, string> Parents = new() { ["Anime"] = "Movie Series", ["Manga"] = "Book", ["LightNovel"] = "Book" };

        // The other modules, in groups that open and close.
        private static readonly (string Title, string Icon, string[] Keys)[] Extras =
        [
            ("setup.modules.discovery", "search", ["DiscoverCache", "LocalDiscovery"]),
            ("setup.modules.personal", "users", ["Notifications", "Collections", "Calendar", "Statistics", "UnitGroups", "HomeLayout", "Franchises", "Profiles", "ProgressSync", "Devices", "Registration"]),
            ("setup.modules.operations", "settings", ["Diagnostics"])
        ];

        private InstanceSettingsDetailsV1? _settings;
        private SetupProgressV1? _progress;
        private IReadOnlyList<SetupModuleV1> _modules = [];
        private IReadOnlyList<InstanceModuleDetailsV1> _details = [];
        private IReadOnlyList<InstanceMediaDetailsV1> _media = [];
        private SaveSetupModulesDataV1? _input;
        private HashSet<string>? _taken;

        protected override void Define(Page page)
        {
            page.Title("setup.step.Modules");
            page.Asset("Pages/Setup/Instance/Instance.entry.ts");
            page.Asset("Pages/Setup/Instance/Instance.entry.scss");
            page.Asset("Framework/Components/OptionList.entry.scss");
            page.Asset("Framework/Components/Truncated.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            if (_settings is not { } settings || _progress is not { } progress) return;

            // After a takeover the media types start as what was taken over: the types that hold titles stay on, the others start off.
            var input = _input ?? new SaveSetupModulesDataV1(settings.Name, settings.Version, progress.Version,
                new BaseDataList<InstanceModuleInputV1>(_modules.Select(module => new InstanceModuleInputV1(module.Id, module.IsEnabled, module.Version))),
                new BaseDataList<UpdateInstanceMediaDataV1>(_media.Select(media => new UpdateInstanceMediaDataV1(media.Key, _taken is null || media.IsSubtype ? media.IsEnabled : _taken.Contains(media.Key), media.Version))));
            var presets = new Div(
                new OptionPreset(new Icon("spark", assets, 18), "setup.preset.complete", "setup.preset.complete.line", "complete"),
                new OptionPreset(new Icon("film", assets, 18), "setup.preset.mediaServer", "setup.preset.mediaServer.line", "mediaServer"),
                new OptionPreset(new Icon("download", assets, 18), "setup.preset.requests", "setup.preset.requests.line", "requests"))
                .Class("option-presets").Attribute("hidden", "hidden").Attribute("role", "group").TextAttribute("aria-label", "setup.presets");

            var keys = _modules.ToDictionary(module => module.Id, module => module.Key);
            var ids = _modules.ToDictionary(module => module.Key, module => module.Id);
            var details = _details.ToDictionary(module => module.Key);
            var enabled = input.Modules.Where(module => keys.ContainsKey(module.Id)).ToDictionary(module => keys[module.Id], module => module.IsEnabled);
            var mediaOn = input.Media.ToDictionary(media => media.Key, media => media.IsEnabled);
            var mediaInfo = _media.ToDictionary(media => media.Key);

            // One module as a row; the row keeps its place in the posted list through its id.
            void ModuleRow(Form<SaveSetupModulesDataV1> fields, string key, Func<OptionRow, OptionRow> style)
            {
                if (!ids.TryGetValue(key, out var id) || !details.TryGetValue(key, out var detail)) return;
                fields.Rows(item => item.Modules, (row, module) =>
                {
                    row.Hidden(item => item.Id);
                    row.Hidden(item => item.Version);
                    row.Switch(item => item.IsEnabled, "instance.module." + key, control => style(new OptionRow(texts["instance.module." + key], texts["setup.module.help." + key], control))
                        .Data("module", key).Data("requires", string.Join(' ', detail.Requires)));
                }, module => module.Id == id);
            }

            string Count(IEnumerable<string> moduleKeys, IEnumerable<string> mediaKeys)
            {
                var modulesList = moduleKeys.ToList();
                var mediaList = mediaKeys.ToList();
                var on = modulesList.Count(key => enabled.GetValueOrDefault(key)) + mediaList.Count(key => mediaOn.GetValueOrDefault(key));
                return string.Format(CultureInfo.CurrentCulture, texts["setup.group.active"], on, modulesList.Count + mediaList.Count);
            }

            // The groups of the other modules; a module no group names goes with the administration ones.
            var placed = new HashSet<string>(Basics.Concat(Subs.Values.SelectMany(group => group)).Concat(Extras.SelectMany(extra => extra.Keys)));
            var leftover = _modules.Select(module => module.Key).Where(key => !placed.Contains(key)).ToArray();
            var extraGroups = Extras.Select((extra, index) => (extra.Title, extra.Icon, Keys: extra.Keys.Concat(index == Extras.Length - 1 ? leftover : []).Where(ids.ContainsKey).ToArray())).ToList();

            var form = new Form<SaveSetupModulesDataV1>(fields =>
            {
                fields.Hidden(item => item.Version);
                fields.Hidden(item => item.SetupVersion);
                fields.Text(item => item.Name, "admin.instance.name");

                fields.Wrap(rows => new Div([.. rows]).Class("option-grid"), grid =>
                {
                    foreach (var key in Basics) ModuleRow(grid, key, row => row.Card());
                });

                fields.Wrap(groups => new Div([.. groups]).Class("option-groups"), all =>
                {
                    foreach (var area in MediaAreas.All)
                    {
                        var types = area.Slugs.Select(slug => MediaAreas.Keys[slug]).Where(mediaInfo.ContainsKey).ToArray();
                        if (types.Length == 0) continue;
                        var moduleKeys = types.SelectMany(type => Subs.GetValueOrDefault(type, [])).Where(ids.ContainsKey).ToArray();
                        all.Wrap(group => new OptionGroup(new Icon(area.Icon, assets, 16), texts[area.LabelKey], Count(moduleKeys, types), true, assets, [.. group]), inside =>
                        {
                            foreach (var type in types)
                            {
                                inside.Wrap(parts => new Div([.. parts]).Class("option-type"), part =>
                                {
                                    part.Rows(item => item.Media, (row, media) =>
                                    {
                                        row.Hidden(item => item.Key);
                                        row.Hidden(item => item.Version);
                                        var label = MediaTypeLabel.Key(media.Key, mediaInfo[media.Key].IsSubtype);
                                        row.Switch(item => item.IsEnabled, label, control =>
                                        {
                                            var option = new OptionRow(texts["setup.type.name." + media.Key], texts["setup.type.help." + media.Key], control).Type().Data("media", media.Key);
                                            return Parents.TryGetValue(media.Key, out var parents) ? option.Data("media-parents", parents) : option;
                                        });
                                    }, media => media.Key == type);
                                    var subs = Subs.GetValueOrDefault(type, []).Where(ids.ContainsKey).ToArray();
                                    if (subs.Length > 0) part.Wrap(rows => new Div([.. rows]).Class("option-subs"), list => { foreach (var key in subs) ModuleRow(list, key, row => row.Sub()); });
                                });
                            }
                        });
                    }
                });

                // Optional modules remain discoverable without opening hidden accordions.
                // Keep one switch per module, in the same order and form as the API.
                fields.Wrap(groups => new Div(new Div(new Strong(new Text("setup.modules.extra"))).Class("setup-modules__extras-heading"), new Div([.. groups]).Class("option-groups option-groups--extras")).Class("setup-modules__extras"), all =>
                {
                    foreach (var (title, icon, groupKeys) in extraGroups)
                    {
                        if (groupKeys.Length == 0) continue;
                        all.Wrap(group => new OptionGroup(new Icon(icon, assets, 16), texts[title], Count(groupKeys, []), true, assets, [.. group]), inside =>
                        {
                            foreach (var key in groupKeys) ModuleRow(inside, key, row => row.Card());
                        });
                    }
                });

                fields.Submit("setup.next");
            }, "/Setup/Instance", csrf, input, FormError);
            page.Content.Add(new Div(presets, new Div(form).Class("setup-modules__form")).Class("setup-modules")
                .Attribute("data-setup-needed", texts["settings.module.neededBy"]).Attribute("data-setup-count", texts["setup.group.active"])
                .Attribute("data-setup-names", JsonSerializer.Serialize(_details.ToDictionary(module => module.Key, module => texts["instance.module." + module.Key])))
                .Attribute("data-setup-presets", JsonSerializer.Serialize(new Dictionary<string, string[]>
                {
                    ["complete"] = [.. _modules.Where(module => module.Complete).Select(module => module.Key)],
                    ["mediaServer"] = [.. _modules.Where(module => module.MediaServer).Select(module => module.Key)],
                    ["requests"] = [.. _modules.Where(module => module.Requests).Select(module => module.Key)]
                })));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            if (_progress?.IsCompleted is true) return LocalRedirect("/");
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? name, long version, long setupVersion, List<ModuleRow> modules, List<MediaRow> media)
        {
            // The page shows every row as posted; only the media that changed are saved, so a subtype whose parent was switched off is not asked to turn on again.
            _media = await readMedia.ExecuteAsync<BaseDataList<InstanceMediaDetailsV1>>(new ReadInstanceMediaParametersV1(), HttpContext.RequestAborted);
            var current = _media.ToDictionary(item => item.Key);
            var posted = (media ?? []).Select(item => new UpdateInstanceMediaDataV1(item.Key ?? "", item.IsEnabled, item.Version)).ToList();
            var moduleRows = new BaseDataList<InstanceModuleInputV1>(modules.Select(module => new InstanceModuleInputV1(module.Id, module.IsEnabled, module.Version)));
            _input = new SaveSetupModulesDataV1(name ?? "", version, setupVersion, moduleRows, new BaseDataList<UpdateInstanceMediaDataV1>(posted));
            var changed = new BaseDataList<UpdateInstanceMediaDataV1>(posted.Where(item => !current.TryGetValue(item.Key, out var now) || now.IsEnabled != item.IsEnabled));
            await save.ExecuteAsync<UpdateResult>(new SetupProgressParametersV1(), new SaveSetupModulesDataV1(name ?? "", version, setupVersion, moduleRows, changed), HttpContext.RequestAborted);
            return LocalRedirect("/Setup/Progress");
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _progress = await readProgress.ExecuteAsync<SetupProgressV1>(cancellationToken: ct);
            _settings = await readSettings.ExecuteAsync<InstanceSettingsDetailsV1>(new ReadInstanceSettingsParametersV1(), ct);
            _modules = await readModules.ExecuteAsync<BaseDataList<SetupModuleV1>>(cancellationToken: ct);
            _details = await readDetails.ExecuteAsync<BaseDataList<InstanceModuleDetailsV1>>(new ReadInstanceModulesParametersV1(), ct);
            if (_media.Count == 0) _media = await readMedia.ExecuteAsync<BaseDataList<InstanceMediaDetailsV1>>(new ReadInstanceMediaParametersV1(), ct);
            if (_progress is { SetupStartTypeId: SetupStartType.Takeover } && _progress.SetupStepTypeId <= SetupStepType.Modules && _input is null)
            {
                var taken = new HashSet<string>(StringComparer.Ordinal);
                foreach (var type in Enum.GetValues<MediaType>())
                {
                    if ((await readWorks.ExecuteAsync<PageResult<WorkListItemV1>>(new ReadWorksParametersV1(1, 1, type), ct)).Items.Count > 0) taken.Add(type.ToString());
                }

                _taken = taken.Count > 0 ? taken : null;
            }
        }

        public sealed record ModuleRow(long Id, bool IsEnabled, long Version);

        public sealed record MediaRow(string? Key, bool IsEnabled, long Version);
    }
}
