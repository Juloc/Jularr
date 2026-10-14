using System.Globalization;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Backend.Service.Public.Localization.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Instance
{
    public sealed class InstanceModel(ReadInstanceSettingsV1 read, UpdateInstanceSettingsV1 update, ReadUiLocalesV1 readLocales,
        ReadInstanceModulesV1 readModules, UpdateInstanceModuleV1 updateModule, ReadInstanceMediaV1 readMedia, UpdateInstanceMediaV1 updateMedia, IAntiforgery antiforgery,
        UiText texts) : JularrPage
    {
        private InstanceSettingsDetailsV1? _settings;
        private IReadOnlyList<UiLocaleDetailsV1> _locales = [];
        private UpdateInstanceSettingsDataV1? _input;
        private IReadOnlyList<InstanceModuleDetailsV1> _modules = [];
        private UpdateInstanceModuleDataV1? _moduleInput;
        private long? _moduleId;
        private IReadOnlyList<InstanceMediaDetailsV1> _media = [];
        private UpdateInstanceMediaDataV1? _mediaInput;

        private string Home => SetupWizard.Address(Request, SetupStepType.Instance, "/Admin/Instance");

        protected override void Define(Page page)
        {
            page.Title("admin.instance.title");
            page.Navigation("nav.admin");
            if (SetupWizard.IsActive(Request)) page.Content.Add(SetupWizard.Frame(SetupStepType.Instance, antiforgery.GetAndStoreTokens(HttpContext).RequestToken));

            // Edit supported settings only.
            if (_settings is { } settings)
            {
                var options = _locales.Select(locale => new SelectOption(locale.Id.ToString(CultureInfo.InvariantCulture), locale.Name));
                page.Content.Add(new Form<UpdateInstanceSettingsDataV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Text(item => item.Name, "admin.instance.name");
                    form.Select(item => item.UiLocaleId, "profile.field.uiLocale", options);
                    form.Number(item => item.ReadingCacheMegabytes, "admin.instance.readingCacheMegabytes");
                    form.Number(item => item.ReadingCacheWorks, "admin.instance.readingCacheWorks");
                    form.Select(item => item.UsersConfirmSources, "admin.instance.usersConfirmSources",
                        [new SelectOption("True", texts["common.yes"]), new SelectOption("False", texts["common.no"])]);
                    form.Number(item => item.LibraryScanIntervalHours, "admin.instance.libraryScanIntervalHours");
                    form.Number(item => item.HistoryRetentionDays, "admin.instance.historyRetentionDays");
                    form.Number(item => item.HistoryMegabytes, "admin.instance.historyMegabytes");
                    if (texts.HasModule("Collections")) form.Number(item => item.CollectionLimit, "admin.instance.collectionLimit");
                    if (texts.HasModule("UnitGroups")) form.Number(item => item.UnitGroupLimit, "admin.instance.unitGroupLimit");
                    if (texts.HasModule("Subtitles")) form.Text(item => item.SubtitleLanguages, "admin.instance.subtitleLanguages");
                    form.Submit("common.save");
                }, Home, antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    _input ?? new UpdateInstanceSettingsDataV1(settings.Name, settings.UiLocaleId, settings.Version, settings.ReadingCacheMegabytes, settings.ReadingCacheWorks, settings.UsersConfirmSources,
                        settings.LibraryScanIntervalHours, settings.HistoryRetentionDays, settings.HistoryMegabytes, settings.CollectionLimit,
                        settings.UnitGroupLimit, settings.SubtitleLanguages), _moduleId is null && _mediaInput is null ? FormError : null));

                if (TempData["InstanceSaved"] is true)
                {
                    page.Content.Add(new P(new Text("admin.instance.saved")).Attribute("role", "status"));
                }
            }
            else if (FormError is not null)
            {
                page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
            }

            // Only implemented module controls.
            var stateOptions = new[] { new SelectOption("True", texts["admin.providers.enabled"]), new SelectOption("False", texts["admin.providers.disabled"]) };
            var moduleForms = new List<IUiComponent> { new P(new Text("admin.instance.module.retained")) };
            foreach (var module in _modules)
            {
                moduleForms.Add(new P(new Text("instance.module." + module.Key)));
                moduleForms.Add(new Form<UpdateInstanceModuleDataV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Select(item => item.IsEnabled, "admin.instance.module.state", stateOptions);
                    form.Submit("common.save");
                }, Home + "?handler=Module&instanceModuleId=" + module.Id.ToString(CultureInfo.InvariantCulture), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    _moduleId == module.Id ? _moduleInput : new UpdateInstanceModuleDataV1(module.IsEnabled, module.Version), _moduleId == module.Id ? FormError : null));
            }

            if (_modules.Count > 0) page.Content.Add(new UiSection("admin.instance.modules", moduleForms));

            // Subtypes take their Works out of the parent lists; an enabled one needs its own library.
            var mediaForms = new List<IUiComponent>();
            foreach (var media in _media)
            {
                mediaForms.Add(new P(new Text(MediaTypeLabel.Key(media.Key, media.IsSubtype))));
                if (media.IsEnabled && !media.HasLibrary) mediaForms.Add(new P(new Text("admin.instance.media.noLibrary")).Attribute("role", "status"));
                mediaForms.Add(new Form<UpdateInstanceMediaDataV1>(form =>
                {
                    form.Hidden(item => item.Key);
                    form.Hidden(item => item.Version);
                    form.Select(item => item.IsEnabled, "admin.instance.media.state", stateOptions);
                    form.Submit("common.save");
                }, Home + "?handler=Media", antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    _mediaInput?.Key == media.Key ? _mediaInput : new UpdateInstanceMediaDataV1(media.Key, media.IsEnabled, media.Version), _mediaInput?.Key == media.Key ? FormError : null));
            }

            if (_media.Count > 0) page.Content.Add(new UiSection("admin.instance.media", mediaForms));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? name, long uiLocaleId, long version, int? readingCacheMegabytes, int? readingCacheWorks,
            bool? usersConfirmSources, int? libraryScanIntervalHours, int? historyRetentionDays, int? historyMegabytes,
            int? collectionLimit, int? unitGroupLimit, string? subtitleLanguages)
        {
            // Save through the service.
            _input = new UpdateInstanceSettingsDataV1(name ?? "", uiLocaleId, version, readingCacheMegabytes, readingCacheWorks, usersConfirmSources, libraryScanIntervalHours,
                historyRetentionDays, historyMegabytes, collectionLimit, unitGroupLimit,
                subtitleLanguages is null ? null : [subtitleLanguages]);
            await update.ExecuteAsync<UpdateResult>(new UpdateInstanceSettingsParametersV1(), _input, HttpContext.RequestAborted);
            TempData["InstanceSaved"] = true;
            return LocalRedirect(Home);
        }

        protected override async Task LoadDataAsync()
        {
            var token = HttpContext.RequestAborted;
            _settings = await read.ExecuteAsync<InstanceSettingsDetailsV1>(new ReadInstanceSettingsParametersV1(), token);
            _locales = await readLocales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: token);
            _modules = await readModules.ExecuteAsync<BaseDataList<InstanceModuleDetailsV1>>(new ReadInstanceModulesParametersV1(), token);
            _media = await readMedia.ExecuteAsync<BaseDataList<InstanceMediaDetailsV1>>(new ReadInstanceMediaParametersV1(), token);
        }

        public async Task<IActionResult> OnPostModuleAsync(long instanceModuleId, [BindRequired] bool isEnabled, long version)
        {
            _moduleId = instanceModuleId;
            _moduleInput = new UpdateInstanceModuleDataV1(isEnabled, version);
            await updateModule.ExecuteAsync<UpdateResult>(new UpdateInstanceModuleParametersV1(instanceModuleId), _moduleInput, HttpContext.RequestAborted);
            return LocalRedirect(Home);
        }

        public async Task<IActionResult> OnPostMediaAsync(string? key, [BindRequired] bool isEnabled, long version)
        {
            _mediaInput = new UpdateInstanceMediaDataV1(key ?? "", isEnabled, version);
            await updateMedia.ExecuteAsync<UpdateResult>(new UpdateInstanceMediaParametersV1(), _mediaInput, HttpContext.RequestAborted);
            return LocalRedirect(Home);
        }
    }
}
