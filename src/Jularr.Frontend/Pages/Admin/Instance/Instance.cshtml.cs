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

namespace Jularr.Frontend.Pages.Admin.Instance
{
    // The instance's own settings in sections of one form, saved by the page's pill; what only matters while a module is on appears only then.
    public sealed class InstanceModel(ReadInstanceSettingsV1 read, UpdateInstanceSettingsV1 update, ReadUiLocalesV1 readLocales, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private InstanceSettingsDetailsV1? _settings;
        private IReadOnlyList<UiLocaleDetailsV1> _locales = [];
        private UpdateInstanceSettingsDataV1? _input;

        private string Home => SetupWizard.Address(Request, SetupStepType.Instance, "/Admin/Instance");

        protected override void Define(Page page)
        {
            page.Title("admin.instance.title");
            page.Navigation("nav.admin");
            page.Asset("Framework/Components/Form.entry.ts");
            if (SetupWizard.IsActive(Request)) page.Content.Add(SetupWizard.Frame(SetupStepType.Instance));

            if (texts.Can(null, "Translation"))
            {
                page.Content.Add(new A(new ValueText("Local translation models")).Attribute("href", "/Admin/Instance/Translation"));
            }

            if (_settings is { } settings)
            {
                var options = _locales.Select(locale => new SelectOption(locale.Id.ToString(CultureInfo.InvariantCulture), locale.Name));
                page.Content.Add(new Form<UpdateInstanceSettingsDataV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Section("settings.instance.general", general =>
                    {
                        general.Text(item => item.Name, "admin.instance.name");
                        general.Select(item => item.UiLocaleId, "profile.field.uiLocale", options);
                        general.Select(item => item.UsersConfirmSources, "admin.instance.usersConfirmSources",
                            [new SelectOption("True", texts["common.yes"]), new SelectOption("False", texts["common.no"])]);
                        if (texts.Can(null, "Subtitles")) general.Text(item => item.SubtitleLanguages, "admin.instance.subtitleLanguages");
                        if (texts.Can(null, "Collections")) general.Number(item => item.CollectionLimit, "admin.instance.collectionLimit");
                        if (texts.Can(null, "UnitGroups")) general.Number(item => item.UnitGroupLimit, "admin.instance.unitGroupLimit");
                    });

                    // How often the background work looks again.
                    form.Section("settings.instance.schedules", schedules =>
                    {
                        if (texts.Can(null, "Library")) schedules.Range(item => item.LibraryScanIntervalHours, "admin.instance.libraryScanIntervalHours");
                        schedules.Range(item => item.BackupIntervalHours, "admin.instance.backupIntervalHours");
                        schedules.Range(item => item.BackupKeep, "admin.instance.backupKeep");
                        schedules.Range(item => item.ProviderSyncIntervalMinutes, "admin.instance.providerSyncIntervalMinutes");
                        if (texts.Can(null, "Acquisition")) schedules.Range(item => item.WantedSearchIntervalMinutes, "admin.instance.wantedSearchIntervalMinutes");
                    });

                    form.Section("settings.instance.history", history =>
                    {
                        if (texts.Can(null, "Reading"))
                        {
                            history.Number(item => item.ReadingCacheMegabytes, "admin.instance.readingCacheMegabytes", affix: FieldAffix.Unit("unit.megabytes"));
                            history.Number(item => item.ReadingCacheWorks, "admin.instance.readingCacheWorks");
                        }

                        history.Number(item => item.HistoryRetentionDays, "admin.instance.historyRetentionDays", affix: FieldAffix.Unit("unit.days"));
                        history.Number(item => item.HistoryMegabytes, "admin.instance.historyMegabytes", affix: FieldAffix.Unit("unit.megabytes"));
                    });

                    form.Section("settings.instance.access", access =>
                    {
                        if (texts.Can(null, "Devices")) access.Text(item => item.PublicUrl, "admin.instance.publicUrl");
                        else access.Hidden(item => item.PublicUrl);
                        access.Text(item => item.TrustedProxies, "admin.instance.trustedProxies");
                    });

                    form.Save();
                }, Home, antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    _input ?? new UpdateInstanceSettingsDataV1(settings.Name, settings.UiLocaleId, settings.Version, settings.ReadingCacheMegabytes, settings.ReadingCacheWorks, settings.UsersConfirmSources,
                        settings.LibraryScanIntervalHours, settings.HistoryRetentionDays, settings.HistoryMegabytes, settings.CollectionLimit,
                        settings.UnitGroupLimit, settings.SubtitleLanguages, settings.PublicUrl, settings.TrustedProxies, settings.BackupIntervalHours, settings.BackupKeep,
                        settings.ProviderSyncIntervalMinutes, settings.WantedSearchIntervalMinutes), FormError));
            }
            else if (FormError is not null)
            {
                page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? name, long uiLocaleId, long version, int? readingCacheMegabytes, int? readingCacheWorks,
            bool? usersConfirmSources, int? libraryScanIntervalHours, int? historyRetentionDays, int? historyMegabytes,
            int? collectionLimit, int? unitGroupLimit, string? subtitleLanguages, string? publicUrl, string? trustedProxies, int? backupIntervalHours, int? backupKeep,
            int? providerSyncIntervalMinutes, int? wantedSearchIntervalMinutes)
        {
            // Save through the service.
            _input = new UpdateInstanceSettingsDataV1(name ?? "", uiLocaleId, version, readingCacheMegabytes, readingCacheWorks, usersConfirmSources, libraryScanIntervalHours,
                historyRetentionDays, historyMegabytes, collectionLimit, unitGroupLimit,
                subtitleLanguages is null ? null : [subtitleLanguages], publicUrl, trustedProxies is null ? null : [trustedProxies], backupIntervalHours, backupKeep,
                providerSyncIntervalMinutes, wantedSearchIntervalMinutes);
            await update.ExecuteAsync<UpdateResult>(new UpdateInstanceSettingsParametersV1(), _input, HttpContext.RequestAborted);
            return LocalRedirect(Home);
        }

        protected override async Task LoadDataAsync()
        {
            var token = HttpContext.RequestAborted;
            _settings = await read.ExecuteAsync<InstanceSettingsDetailsV1>(new ReadInstanceSettingsParametersV1(), token);
            _locales = await readLocales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: token);
        }
    }
}
