using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Data.User.Home.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Service.Public.Localization.V1;
using Jularr.Backend.Service.User.Home.V1;
using Jularr.Backend.Service.User.Profile.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Appearance;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.User.Settings
{
    public enum HomeShelfAction : byte
    {
        Up = 1,
        Down = 2,
        Hide = 3,
        Show = 4
    }

    public sealed class SettingsModel(ReadAuthenticationV1 authentication, ReadProfileV1 readProfile, UpdateProfileV1 updateProfile, ReadProfileMediaV1 readMedia, UpdateProfileMediaV1 updateMedia,
        UpdateProfilePinV1 updatePin, RemoveProfilePinV1 removePin, DeleteProfileV1 deleteProfile, ReadUiLocalesV1 readLocales, ReadHomeLayoutV1 readLayout, SaveHomeLayoutV1 saveLayout,
        IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private IReadOnlyList<HomeShelfV1> _layout = [];
        private AuthenticationResultV1? _authentication;
        private ReadProfileResultV1? _profile;
        private IReadOnlyList<UiLocaleDetailsV1> _locales = [];
        private UpdateProfileDataV1? _input;
        private IReadOnlyList<ProfileMediaV1> _media = [];
        private bool _savingMedia;
        private bool _savingPin;

        protected override void Define(Page page)
        {
            page.Title("settings.title");
            page.Navigation("nav.settings");
            page.Asset("Modules/Appearance/appearance.entry.scss");
            page.Content.Add(new UiSection("appearance.title", [new Appearance()]));

            // Show only supported profile settings.
            if (_profile is not null)
            {
                var content = new List<IUiComponent>();
                if (_authentication!.Permissions.Contains("profile.self.update", StringComparer.Ordinal))
                {
                    var options = _locales.Select(locale => new SelectOption(locale.Id.ToString(CultureInfo.InvariantCulture), locale.Name));
                    var data = _input ?? new UpdateProfileDataV1 { DisplayName = _profile.DisplayName, UiLocaleId = _profile.UiLocaleId };
                    content.Add(new Form<UpdateProfileDataV1>(form =>
                    {
                        form.Text(x => x.DisplayName, "profile.field.displayName", "nickname");
                        form.Select(x => x.UiLocaleId, "profile.field.uiLocale", options);
                        form.Submit("profile.settings.save");
                    }, Url.Page("/User/Settings/Settings"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, data, _savingMedia || _savingPin ? null : FormError));

                    // Hidden types leave the profile's lists; they stay reachable.
                    if (_media.Count > 0)
                    {
                        content.Add(new Form<UpdateProfileMediaDataV1>(form =>
                        {
                            form.Choices(x => x.HiddenKeys, "profile.media.hidden", _media.Select(item => new SelectOption(item.Key, texts[MediaTypeLabel.Key(item.Key, item.IsSubtype)])));
                            form.Submit("profile.settings.save");
                        }, Url.Page("/User/Settings/Settings", "Media"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                            new UpdateProfileMediaDataV1([.. _media.Where(item => item.IsHidden).Select(item => item.Key)]), _savingMedia ? FormError : null));
                    }

                    // PIN and deletion belong to the Profiles module.
                    if (texts.CanManageProfiles())
                    {
                        // A PIN guards switching to this profile.
                        content.Add(new Form<UpdateProfilePinDataV1>(form =>
                        {
                            form.Pin(x => x.Pin, "account.profile.pin");
                            form.Submit("profile.pin.save");
                        }, Url.Page("/User/Settings/Settings", "Pin"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, null, _savingPin ? FormError : null));
                        if (_profile.HasPin)
                        {
                            content.Add(new Form<UpdateProfilePinDataV1>(form => form.Submit("profile.pin.remove"), Url.Page("/User/Settings/Settings", "PinRemove"),
                                antiforgery.GetAndStoreTokens(HttpContext).RequestToken));
                        }

                        // Deleting asks once more; nothing of the profile remains.
                        if (Request.Query["delete"] == "true")
                        {
                            content.Add(new P(new Text("profile.delete.warning")).Attribute("role", "alert"));
                            content.Add(new Form<UpdateProfilePinDataV1>(form => form.Submit("profile.delete.confirm"), Url.Page("/User/Settings/Settings", "Delete"),
                                antiforgery.GetAndStoreTokens(HttpContext).RequestToken));
                        }
                        else
                        {
                            content.Add(new A(new Text("profile.delete")).Class("button").Attribute("href", Url.Page("/User/Settings/Settings", new { delete = true })!));
                        }
                    }
                }
                else
                {
                    if (FormError is not null) content.Add(new P(new Text(FormError.ResourceKey)).Class("field-error").Attribute("role", "alert"));
                    content.Add(new P(new ValueText(_profile.DisplayName)));
                    var locale = _locales.FirstOrDefault(item => item.Id == _profile.UiLocaleId);
                    if (locale is not null) content.Add(new P(new ValueText(locale.Name)));
                }

                if (TempData["ProfileSaved"] is true)
                {
                    content.Add(new P(new Text("profile.settings.saved")).Attribute("role", "status"));
                }

                page.Content.Add(new UiSection("profile.settings.title", content));
            }
            else
            {
                page.Content.Add(new P(new Text("account.profile.required")).Attribute("role", "status"));
                page.Content.Add(new A(new Text("account.profile.title")).Attribute("href", "/Account/Profiles?returnUrl=%2FSettings"));
                if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Class("field-error").Attribute("role", "alert"));
            }

            // Home shelves in their order; each moves or hides with one button.
            if (_layout.Count > 0)
            {
                var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                var positions = _layout.Select((shelf, index) => (shelf.HomeShelfTypeId, index)).ToDictionary(item => item.HomeShelfTypeId, item => item.index);
                page.Content.Add(new UiSection("home.layout", [new Table<HomeShelfV1>(_layout)
                    .Resource(shelf => shelf.HomeShelfTypeId, "home.shelf", type => "home.shelf." + type)
                    .Content("table.status", shelf => new Text(shelf.IsHidden ? "home.hidden" : "home.shown"))
                    .Content("common.actions", shelf =>
                    {
                        var index = positions[shelf.HomeShelfTypeId];
                        return new Div([
                            .. index > 0 ? new IUiComponent[] { HomeAction(shelf, HomeShelfAction.Up, "home.moveUp", csrf) } : [],
                            .. index + 1 < _layout.Count ? new IUiComponent[] { HomeAction(shelf, HomeShelfAction.Down, "home.moveDown", csrf) } : [],
                            HomeAction(shelf, shelf.IsHidden ? HomeShelfAction.Show : HomeShelfAction.Hide, shelf.IsHidden ? "home.show" : "home.hide", csrf)]);
                    })]));
            }

            page.Content.Add(new UiSection("settings.account", [
                new A(new Text("settings.sessions")).Class("button").Attribute("href", "/Account/Sessions"),
                .. (texts.Can("profile.devices", "Devices") ? new[] { new A(new Text("devices.pair.title")).Class("button").Attribute("href", "/Devices/Pair") } : []),
                .. (texts.CanManageProfiles()
                    ? new[] { new A(new Text("account.profile.title")).Class("button").Attribute("href", "/Account/Profiles?returnUrl=%2FSettings") }
                    : []),
                new A(new Text("account.logout.title")).Class("button").Attribute("href", "/Account/Logout")
            ]));

            // Expose granted administration routes.
            if (_authentication?.Permissions.Contains("storage.read", StringComparer.Ordinal) is true)
            {
                page.Content.Add(new A(new Text("storage.title")).Attribute("href", "/Admin/Storage"));
                page.Content.Add(new A(new Text("setup.storage.title")).Attribute("href", "/Setup/Storage"));
            }

            if (_authentication?.Permissions.Contains("acquisition.manage", StringComparer.Ordinal) is true)
            {
                page.Content.Add(new A(new Text("admin.acquisition.title")).Attribute("href", "/Admin/Acquisition"));
                page.Content.Add(new A(new Text("admin.profiles.title")).Attribute("href", "/Admin/Acquisition/Profiles"));
            }

            if (_authentication?.Permissions.Contains("instance.settings.read", StringComparer.Ordinal) is true)
            {
                page.Content.Add(new A(new Text("admin.instance.title")).Attribute("href", "/Admin/Instance"));
            }

            if (_authentication?.Permissions.Contains("backup.read", StringComparer.Ordinal) is true)
            {
                page.Content.Add(new A(new Text("admin.backup.title")).Attribute("href", "/Admin/Backup"));
            }

            if (_authentication?.Permissions.Contains("accounts.read", StringComparer.Ordinal) is true)
            {
                page.Content.Add(new A(new Text("admin.accounts.title")).Attribute("href", "/Admin/Accounts"));
            }

            if (_authentication?.Permissions.Contains("localization.read", StringComparer.Ordinal) is true)
            {
                page.Content.Add(new A(new Text("admin.localization.title")).Attribute("href", "/Admin/Localization"));
            }

            if (_authentication?.Permissions.Contains("providers.read", StringComparer.Ordinal) is true)
            {
                page.Content.Add(new A(new Text("admin.providers.title")).Attribute("href", "/Admin/Providers"));
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? displayName, long uiLocaleId)
        {
            // Read verified scope only.
            _authentication = await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: HttpContext.RequestAborted);
            var profileId = _authentication!.ProfileId ?? throw new UnauthorizedAccessException("An active profile is required.");
            _input = new UpdateProfileDataV1 { DisplayName = displayName ?? "", UiLocaleId = uiLocaleId };

            // Save through the canonical writer.
            await updateProfile.ExecuteAsync<UpdateResult>(new UpdateProfileParametersV1(profileId), _input, HttpContext.RequestAborted);
            TempData["ProfileSaved"] = true;
            return RedirectToPage("/User/Settings/Settings");
        }

        public async Task<IActionResult> OnPostMediaAsync(string[]? hiddenKeys)
        {
            _savingMedia = true;
            _authentication = await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: HttpContext.RequestAborted);
            var profileId = _authentication!.ProfileId ?? throw new UnauthorizedAccessException("An active profile is required.");
            await updateMedia.ExecuteAsync<UpdateResult>(new UpdateProfileParametersV1(profileId), new UpdateProfileMediaDataV1(hiddenKeys ?? []), HttpContext.RequestAborted);
            TempData["ProfileSaved"] = true;
            return RedirectToPage("/User/Settings/Settings");
        }

        private IUiComponent HomeAction(HomeShelfV1 shelf, HomeShelfAction action, string labelKey, string? csrf) =>
            new Form<HomeLayoutParametersV1>(form => form.Submit(labelKey), "/Settings?handler=Home&shelf=" + shelf.HomeShelfTypeId + "&action=" + action, csrf, new HomeLayoutParametersV1());

        public async Task<IActionResult> OnPostHomeAsync([BindRequired] HomeShelfType shelf, [BindRequired] HomeShelfAction action)
        {
            // The whole order is saved; one shelf moves or changes its visibility.
            var layout = (await readLayout.ExecuteAsync<BaseDataList<HomeShelfV1>>(new HomeLayoutParametersV1(), HttpContext.RequestAborted)).ToList();
            var index = layout.FindIndex(item => item.HomeShelfTypeId == shelf);
            var target = action switch { HomeShelfAction.Up => index - 1, HomeShelfAction.Down => index + 1, _ => index };
            if (index >= 0 && target >= 0 && target < layout.Count)
            {
                (layout[index], layout[target]) = (layout[target], layout[index]);
            }

            bool Hidden(HomeShelfV1 item) => item.HomeShelfTypeId != shelf ? item.IsHidden : action switch { HomeShelfAction.Hide => true, HomeShelfAction.Show => false, _ => item.IsHidden };
            var hidden = layout.Where(Hidden).Select(item => item.HomeShelfTypeId.ToString()).ToArray();
            await saveLayout.ExecuteAsync<UpdateResult>(new HomeLayoutParametersV1(), new SaveHomeLayoutDataV1([.. layout.Select(item => item.HomeShelfTypeId.ToString())], hidden),
                HttpContext.RequestAborted);
            return RedirectToPage("/User/Settings/Settings");
        }

        public async Task<IActionResult> OnPostPinAsync(string? pin)
        {
            _savingPin = true;
            await updatePin.ExecuteAsync<UpdateResult>(await ActiveProfileAsync(), new UpdateProfilePinDataV1(pin ?? ""), HttpContext.RequestAborted);
            TempData["ProfileSaved"] = true;
            return RedirectToPage("/User/Settings/Settings");
        }

        public async Task<IActionResult> OnPostPinRemoveAsync()
        {
            _savingPin = true;
            await removePin.ExecuteAsync<UpdateResult>(await ActiveProfileAsync(), default, HttpContext.RequestAborted);
            TempData["ProfileSaved"] = true;
            return RedirectToPage("/User/Settings/Settings");
        }

        public async Task<IActionResult> OnPostDeleteAsync()
        {
            await deleteProfile.ExecuteAsync<UpdateResult>(await ActiveProfileAsync(), default, HttpContext.RequestAborted);
            return Redirect("/Account/Profiles");
        }

        private async Task<UpdateProfileParametersV1> ActiveProfileAsync()
        {
            _authentication = await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: HttpContext.RequestAborted);
            return new UpdateProfileParametersV1(_authentication!.ProfileId ?? throw new UnauthorizedAccessException("An active profile is required."));
        }

        protected override async Task LoadDataAsync()
        {
            var token = HttpContext.RequestAborted;
            _authentication = await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: token);
            if (_authentication.ProfileId is { } profileId)
            {
                _profile = await readProfile.ExecuteAsync<ReadProfileResultV1>(new ReadProfileParametersV1(profileId), token);
                _media = await readMedia.ExecuteAsync<BaseDataList<ProfileMediaV1>>(new ReadProfileParametersV1(profileId), token);
                _locales = await readLocales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: token);
                if (texts.CanArrangeHome())
                {
                    _layout = await readLayout.ExecuteAsync<BaseDataList<HomeShelfV1>>(new HomeLayoutParametersV1(), token);
                }
            }
        }
    }
}
