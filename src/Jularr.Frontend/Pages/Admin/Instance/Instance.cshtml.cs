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
        UiText texts, FrontendAssets assets) : JularrPage
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
            if (SetupWizard.IsActive(Request)) page.Content.Add(SetupWizard.Frame(SetupStepType.Instance));

            // Edit supported settings only.
            if (_settings is { } settings)
            {
                var options = _locales.Select(locale => new SelectOption(locale.Id.ToString(CultureInfo.InvariantCulture), locale.Name));
                page.Content.Add(new Form<UpdateInstanceSettingsDataV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Text(item => item.Name, "admin.instance.name");
                    form.Select(item => item.UiLocaleId, "profile.field.uiLocale", options);
                    form.Number(item => item.ReadingCacheMegabytes, "admin.instance.readingCacheMegabytes", affix: FieldAffix.Unit("unit.megabytes"));
                    form.Number(item => item.ReadingCacheWorks, "admin.instance.readingCacheWorks");
                    form.Select(item => item.UsersConfirmSources, "admin.instance.usersConfirmSources",
                        [new SelectOption("True", texts["common.yes"]), new SelectOption("False", texts["common.no"])]);
                    form.Number(item => item.LibraryScanIntervalHours, "admin.instance.libraryScanIntervalHours", affix: FieldAffix.Unit("unit.hours"));
                    form.Number(item => item.BackupIntervalHours, "admin.instance.backupIntervalHours", affix: FieldAffix.Unit("unit.hours"));
                    form.Number(item => item.BackupKeep, "admin.instance.backupKeep");
                    form.Number(item => item.ProviderSyncIntervalMinutes, "admin.instance.providerSyncIntervalMinutes", affix: FieldAffix.Unit("unit.minutes"));
                    form.Number(item => item.WantedSearchIntervalMinutes, "admin.instance.wantedSearchIntervalMinutes", affix: FieldAffix.Unit("unit.minutes"));
                    form.Number(item => item.HistoryRetentionDays, "admin.instance.historyRetentionDays", affix: FieldAffix.Unit("unit.days"));
                    form.Number(item => item.HistoryMegabytes, "admin.instance.historyMegabytes", affix: FieldAffix.Unit("unit.megabytes"));
                    if (texts.Can(null, "Collections")) form.Number(item => item.CollectionLimit, "admin.instance.collectionLimit");
                    if (texts.Can(null, "UnitGroups")) form.Number(item => item.UnitGroupLimit, "admin.instance.unitGroupLimit");
                    if (texts.Can(null, "Subtitles")) form.Text(item => item.SubtitleLanguages, "admin.instance.subtitleLanguages", affix: new FieldAffix(new Icon("subtitles", assets, 15)));
                    if (texts.Can(null, "Devices")) form.Text(item => item.PublicUrl, "admin.instance.publicUrl");
                    else form.Hidden(item => item.PublicUrl);
                    form.Text(item => item.TrustedProxies, "admin.instance.trustedProxies");
                    form.Save();
                }, Home, antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    _input ?? new UpdateInstanceSettingsDataV1(settings.Name, settings.UiLocaleId, settings.Version, settings.ReadingCacheMegabytes, settings.ReadingCacheWorks, settings.UsersConfirmSources,
                        settings.LibraryScanIntervalHours, settings.HistoryRetentionDays, settings.HistoryMegabytes, settings.CollectionLimit,
                        settings.UnitGroupLimit, settings.SubtitleLanguages, settings.PublicUrl, settings.TrustedProxies, settings.BackupIntervalHours, settings.BackupKeep,
                        settings.ProviderSyncIntervalMinutes, settings.WantedSearchIntervalMinutes), _moduleId is null && _mediaInput is null ? FormError : null));
            }
            else if (FormError is not null)
            {
                page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
            }

            // Only implemented module controls.
            var stateOptions = new[] { new SelectOption("True", texts["admin.providers.enabled"]), new SelectOption("False", texts["admin.providers.disabled"]) };
            var moduleForms = new List<IUiComponent> { new P(new Text("admin.instance.module.retained")) };
            if (!SetupWizard.IsActive(Request) && texts.Can("instance.settings.read"))
            {
                moduleForms.Add(new A(new Text("speech.models.title")).Class("button").Attribute("href", "/Admin/Instance/Speech"));
            }
            foreach (var module in _modules)
            {
                moduleForms.Add(new Form<UpdateInstanceModuleDataV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Select(item => item.IsEnabled, "instance.module." + module.Key, stateOptions);
                    form.Save();
                }, Home + "?handler=Module&instanceModuleId=" + module.Id.ToString(CultureInfo.InvariantCulture), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    _moduleId == module.Id ? _moduleInput : new UpdateInstanceModuleDataV1(module.IsEnabled, module.Version), _moduleId == module.Id ? FormError : null));
            }

            if (_modules.Count > 0) page.Content.Add(new UiSection("admin.instance.modules", moduleForms));

            // Subtypes take their Works out of the parent lists; an enabled one needs its own library.
            var mediaForms = new List<IUiComponent>();
            foreach (var media in _media)
            {
                mediaForms.Add(new Form<UpdateInstanceMediaDataV1>(form =>
                {
                    form.Hidden(item => item.Key);
                    form.Hidden(item => item.Version);
                    form.Select(item => item.IsEnabled, MediaTypeLabel.Key(media.Key, media.IsSubtype), stateOptions);
                    form.Save();
                }, Home + "?handler=Media", antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    _mediaInput?.Key == media.Key ? _mediaInput : new UpdateInstanceMediaDataV1(media.Key, media.IsEnabled, media.Version), _mediaInput?.Key == media.Key ? FormError : null));
                if (media.IsEnabled && !media.HasLibrary) mediaForms.Add(new P(new Text("admin.instance.media.noLibrary")).Attribute("role", "status"));
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
            int? collectionLimit, int? unitGroupLimit, string? subtitleLanguages, string? publicUrl, int? backupIntervalHours, int? backupKeep, int? providerSyncIntervalMinutes,
            int? wantedSearchIntervalMinutes)
        {
            // Save through the service.
            _input = new UpdateInstanceSettingsDataV1(name ?? "", uiLocaleId, version, readingCacheMegabytes, readingCacheWorks, usersConfirmSources, libraryScanIntervalHours,
                historyRetentionDays, historyMegabytes, collectionLimit, unitGroupLimit,
                subtitleLanguages is null ? null : [subtitleLanguages], publicUrl, null, backupIntervalHours, backupKeep, providerSyncIntervalMinutes, wantedSearchIntervalMinutes);
            await update.ExecuteAsync<UpdateResult>(new UpdateInstanceSettingsParametersV1(), _input, HttpContext.RequestAborted);
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
