using System.Globalization;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Artwork.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Artwork.V1;
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
        ReadArtworkSettingsV1 readArtwork, UpdateArtworkSettingsV1 updateArtwork,
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
        private ArtworkSettingsDetailsV1? _artwork;
        private UpdateArtworkSettingsDataV1? _artworkInput;

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
                    form.Number(item => item.ReadingCacheMegabytes, "admin.instance.readingCacheMegabytes");
                    form.Number(item => item.ReadingCacheWorks, "admin.instance.readingCacheWorks");
                    form.Select(item => item.UsersConfirmSources, "admin.instance.usersConfirmSources",
                        [new SelectOption("True", texts["common.yes"]), new SelectOption("False", texts["common.no"])]);
                    form.Number(item => item.LibraryScanIntervalHours, "admin.instance.libraryScanIntervalHours");
                    form.Number(item => item.HistoryRetentionDays, "admin.instance.historyRetentionDays");
                    form.Number(item => item.HistoryMegabytes, "admin.instance.historyMegabytes");
                    if (texts.Can(null, "Collections")) form.Number(item => item.CollectionLimit, "admin.instance.collectionLimit");
                    if (texts.Can(null, "UnitGroups")) form.Number(item => item.UnitGroupLimit, "admin.instance.unitGroupLimit");
                    if (texts.Can(null, "Subtitles")) form.Text(item => item.SubtitleLanguages, "admin.instance.subtitleLanguages");
                    if (texts.Can(null, "Devices")) form.Text(item => item.PublicUrl, "admin.instance.publicUrl");
                    else form.Hidden(item => item.PublicUrl);
                    form.Text(item => item.TrustedProxies, "admin.instance.trustedProxies");
                    form.Submit("common.save");
                }, Home, antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    _input ?? new UpdateInstanceSettingsDataV1(settings.Name, settings.UiLocaleId, settings.Version, settings.ReadingCacheMegabytes, settings.ReadingCacheWorks, settings.UsersConfirmSources,
                        settings.LibraryScanIntervalHours, settings.HistoryRetentionDays, settings.HistoryMegabytes, settings.CollectionLimit,
                        settings.UnitGroupLimit, settings.SubtitleLanguages, settings.PublicUrl, settings.TrustedProxies), _moduleId is null && _mediaInput is null ? FormError : null));

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

            // NAS export and browser cache intentionally have separate format controls.
            if (_artwork is { } artwork)
            {
                var nasModes = Enum.GetValues<NasArtworkMode>()
                    .Select(mode => new SelectOption(((byte)mode).ToString(CultureInfo.InvariantCulture), texts["admin.artwork.nas." + mode]));
                var cacheFormats = Enum.GetValues<ArtworkCacheFormat>()
                    .Select(format => new SelectOption(((byte)format).ToString(CultureInfo.InvariantCulture), texts["admin.artwork.cache." + format]));
                var qualities = Enum.GetValues<ArtworkQuality>()
                    .Select(quality => new SelectOption(((byte)quality).ToString(CultureInfo.InvariantCulture), texts["admin.artwork.quality." + quality]));
                var limits = new long[] { 0, 64L * 1048576, 256L * 1048576, 512L * 1048576, 1024L * 1048576,
                    2048L * 1048576, 4096L * 1048576, 8192L * 1048576 };
                if (!limits.Contains(artwork.CacheLimitBytes)) limits = [.. limits, artwork.CacheLimitBytes];
                var budgets = limits.Order().Select(value => new SelectOption(value.ToString(CultureInfo.InvariantCulture),
                    value == 0 ? texts["admin.artwork.cache.disabled"]
                    : value / 1048576 >= 1024 && value % (1024L * 1048576) == 0
                        ? (value / (1024L * 1048576)).ToString(CultureInfo.CurrentCulture) + " GiB"
                        : (value / 1048576).ToString("N0", CultureInfo.CurrentCulture) + " MiB"));

                var values = _artworkInput ?? new UpdateArtworkSettingsDataV1(
                    artwork.NasModeId, artwork.CacheFormatId, artwork.CacheQualityId,
                    artwork.ProgressiveJxl, artwork.ExportToNas, artwork.CacheLimitBytes, artwork.Version);
                page.Content.Add(new UiSection("admin.artwork.title", [
                    new P(new Text("admin.artwork.introduction")),
                    new Form<UpdateArtworkSettingsDataV1>(form =>
                    {
                        form.Hidden(item => item.Version);
                        form.Section("admin.artwork.nas.title", section =>
                        {
                            section.Toggle(item => item.ExportToNas, "admin.artwork.export");
                            section.Select(item => item.NasModeId, "admin.artwork.nas.mode", nasModes);
                            section.Content(new P(new Text("admin.artwork.nas.notice")));
                        });
                        form.Section("admin.artwork.cache.title", section =>
                        {
                            section.Select(item => item.CacheFormatId, "admin.artwork.cache.format", cacheFormats);
                            section.Select(item => item.CacheQualityId, "admin.artwork.cache.quality", qualities);
                            section.Toggle(item => item.ProgressiveJxl, "admin.artwork.progressive");
                            section.Select(item => item.CacheLimitBytes, "admin.artwork.cache.limit", budgets);
                            section.Content(new P(new Text("admin.artwork.cache.notice")));
                        });
                        form.Submit("common.save");
                    }, Home + "?handler=Artwork", antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    values, _artworkInput is null ? null : FormError)
                ]));
                if (TempData["ArtworkSaved"] is true)
                    page.Content.Add(new P(new Text("admin.artwork.saved")).Attribute("role", "status"));
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
            int? collectionLimit, int? unitGroupLimit, string? subtitleLanguages, string? publicUrl)
        {
            // Save through the service.
            _input = new UpdateInstanceSettingsDataV1(name ?? "", uiLocaleId, version, readingCacheMegabytes, readingCacheWorks, usersConfirmSources, libraryScanIntervalHours,
                historyRetentionDays, historyMegabytes, collectionLimit, unitGroupLimit,
                subtitleLanguages is null ? null : [subtitleLanguages], publicUrl);
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
            if (!SetupWizard.IsActive(Request) && texts.Can("instance.settings.read"))
                _artwork = await readArtwork.ExecuteAsync<ArtworkSettingsDetailsV1>(new ArtworkSettingsParametersV1(), token);
        }

        public async Task<IActionResult> OnPostModuleAsync(long instanceModuleId, [BindRequired] bool isEnabled, long version)
        {
            _moduleId = instanceModuleId;
            _moduleInput = new UpdateInstanceModuleDataV1(isEnabled, version);
            await updateModule.ExecuteAsync<UpdateResult>(new UpdateInstanceModuleParametersV1(instanceModuleId), _moduleInput, HttpContext.RequestAborted);
            return LocalRedirect(Home);
        }

        public async Task<IActionResult> OnPostArtworkAsync(NasArtworkMode nasModeId, ArtworkCacheFormat cacheFormatId,
            ArtworkQuality cacheQualityId, bool progressiveJxl, bool exportToNas, long cacheLimitBytes, long version)
        {
            _artworkInput = new UpdateArtworkSettingsDataV1(nasModeId, cacheFormatId, cacheQualityId,
                progressiveJxl, exportToNas, cacheLimitBytes, version);
            await updateArtwork.ExecuteAsync<UpdateResult>(new ArtworkSettingsParametersV1(), _artworkInput, HttpContext.RequestAborted);
            TempData["ArtworkSaved"] = true;
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
