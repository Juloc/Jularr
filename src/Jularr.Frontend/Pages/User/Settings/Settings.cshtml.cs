using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Data.User.Account.V1;
using Jularr.Backend.Data.User.AniList.V1;
using Jularr.Backend.Data.User.Home.V1;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Service.Public.Localization.V1;
using Jularr.Backend.Service.User.Account.V1;
using Jularr.Backend.Service.User.AniList.V1;
using Jularr.Backend.Service.User.Home.V1;
using Jularr.Backend.Service.User.Playback.V1;
using Jularr.Backend.Service.User.Profile.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Appearance;
using Jularr.Frontend.Modules.Speech;
using Jularr.Backend.Data.Enums.Speech;
using Jularr.Backend.Data.User.Speech.V1;
using Jularr.Backend.Service.User.Speech.V1;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.User.Settings
{
    public sealed class SettingsModel(ReadAuthenticationV1 authentication, ReadProfileV1 readProfile, UpdateProfileV1 updateProfile, UpdateProfileFavoriteRequestV1 updateFavoriteRequest, ReadProfileMediaV1 readMedia, UpdateProfileMediaV1 updateMedia,
        UpdateProfilePinV1 updatePin, ChangeAccountPasswordV1 changePassword, RemoveProfilePinV1 removePin, DeleteProfileV1 deleteProfile, ReadUiLocalesV1 readLocales, ReadHomeLayoutV1 readLayout, SaveHomeLayoutV1 saveLayout,
        ReadSpeechPreferencesV1 readSpeech, SaveSpeechPreferencesV1 saveSpeech, ReadAvailableSpeechModelsV1 readSpeechModels, ReadPlaybackPreferencesV1 readPlayback, SavePlaybackPreferencesV1 savePlayback,
        ReadPwaAppearanceV1 readPwaAppearance, SavePwaAccentV1 savePwaAccent,
        ReadAniListConnectionV1 readAniList, ConnectAniListV1 connectAniList, DisconnectAniListV1 disconnectAniList, UpdateAniListPushV1 updateAniListPush,
        IAntiforgery antiforgery, UiText texts, FrontendAssets assets) : JularrPage
    {
        // Speeds the player offers; languages a person is likely to choose, each by its own name.
        private static readonly decimal[] Speeds = [0.25m, 0.5m, 0.75m, 1m, 1.25m, 1.5m, 1.75m, 2m, 3m, 4m];
        private static readonly string[] Languages = ["en", "de", "fr", "es", "it", "pt", "nl", "pl", "sv", "da", "no", "fi", "cs", "hu", "ro", "tr", "el", "ru", "uk", "ar", "he", "hi", "th", "id", "ja", "ko", "zh"];

        private PlaybackPreferencesV1? _playback;
        private SavePlaybackPreferencesDataV1? _playbackInput;
        private IReadOnlyList<HomeShelfV1> _layout = [];
        private AuthenticationResultV1? _authentication;
        private ReadProfileResultV1? _profile;
        private PwaAppearanceV1? _pwa;
        private SavePwaAccentDataV1? _pwaInput;
        private IReadOnlyList<UiLocaleDetailsV1> _locales = [];
        private UpdateProfileDataV1? _input;
        private IReadOnlyList<ProfileMediaV1> _media = [];
        private bool _savingMedia;
        private bool _savingPin;
        private bool _savingPassword;
        private SpeechPreferencesV1? _speech;
        private SaveSpeechPreferencesDataV1? _speechInput;
        private IReadOnlyList<SpeechModelV1> _speechModels = [];
        private AniListConnectionV1? _aniList;
        private bool _savingAniList;

        protected override void Define(Page page)
        {
            page.Title("settings.title");
            page.Navigation("nav.settings");
            page.Asset("Modules/Appearance/appearance.entry.scss");
            page.Asset("Pages/User/Settings/PwaAccent.entry.ts");
            page.Asset("Framework/Components/SortableList.entry.ts");
            page.Asset("Framework/Components/PasswordReveal.entry.ts");
            page.Asset("Modules/Speech/settings.entry.ts");
            page.Asset("Modules/Speech/settings.entry.scss");
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
                        form.Save();
                    }, Url.Page("/User/Settings/Settings"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, data, _savingMedia || _savingPin || _savingPassword || _savingAniList || _speechInput is not null || _playbackInput is not null ? null : FormError));

                    // The installed app icon follows the main profile's saved accent, not this device's local theme.
                    content.Add(new Form<SavePwaAccentDataV1>(form =>
                    {
                        form.Text(x => x.AccentHex, "pwa.profile.accent", "off");
                        form.Save();
                    }, Url.Page("/User/Settings/Settings", "PwaAccent"),
                        antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                        _pwaInput ?? new SavePwaAccentDataV1(_profile.AccentHex), _pwaInput is not null ? FormError : null));
                    if (_pwa?.MainProfileId == _profile.Id)
                        content.Add(new P(new Text("pwa.main.current")).Attribute("role", "status"));
                    else
                        content.Add(new A(new Text("pwa.main.manage")).Attribute("href", "/Account/Profiles?returnUrl=%2FSettings"));

                    // Hidden types leave the profile's lists; they stay reachable.
                    if (_media.Count > 0)
                    {
                        content.Add(new Form<UpdateProfileMediaDataV1>(form =>
                        {
                            form.Choices(x => x.HiddenKeys, "profile.media.hidden", _media.Select(item => new SelectOption(item.Key, texts[MediaTypeLabel.Key(item.Key, item.IsSubtype)])));
                            form.Save();
                        }, Url.Page("/User/Settings/Settings", "Media"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                            new UpdateProfileMediaDataV1([.. _media.Where(item => item.IsHidden).Select(item => item.Key)]), _savingMedia ? FormError : null));
                    }

                    // Anyone signed in changes their own password here; the current one proves who asks.
                    content.Add(new Form<ChangeAccountPasswordDataV1>(form =>
                    {
                        form.Password(x => x.CurrentPassword, "account.password.current", "current-password", new FieldHelp(Trailing: new PasswordReveal(assets)));
                        form.Password(x => x.NewPassword, "account.password.new", "new-password", new FieldHelp("account.register.passwordPlaceholder", "", Trailing: new PasswordReveal(assets)));
                        form.Password(x => x.NewPasswordConfirmation, "account.password.confirm", "new-password", new FieldHelp(Trailing: new PasswordReveal(assets)));
                        form.Submit("account.password.save");
                    }, Url.Page("/User/Settings/Settings", "Password"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, null, _savingPassword ? FormError : null));

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
                        if (texts.CanDeleteProfile() && Request.Query["delete"] == "true")
                        {
                            content.Add(new P(new Text("profile.delete.warning")).Attribute("role", "alert"));
                            content.Add(new Form<UpdateProfilePinDataV1>(form => form.Submit("profile.delete.confirm"), Url.Page("/User/Settings/Settings", "Delete"),
                                antiforgery.GetAndStoreTokens(HttpContext).RequestToken));
                        }
                        else if (texts.CanDeleteProfile())
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

                page.Content.Add(new UiSection("profile.settings.title", content));
                if (_authentication!.Permissions.Contains("profile.self.update", StringComparer.Ordinal))
                {
                    page.Content.Add(new UiSection("profile.favorite.title", [
                        new Form<UpdateProfileFavoriteRequestDataV1>(form =>
                        {
                            form.Toggle(option => option.AutoRequestOnFavorite, "profile.favorite.autoRequest");
                            form.Save();
                        }, Url.Page("/User/Settings/Settings", "FavoriteRequest"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                            new UpdateProfileFavoriteRequestDataV1(_profile.AutoRequestOnFavorite)),
                        new P(new Text("profile.favorite.autoRequestHelp"))
                    ]));
                }
                if (_playback is not null && texts.CanPlay()) page.Content.Add(new UiSection("playback.settings.title", [PlaybackForm(_playback)]));

                // Each profile connects its own AniList account; a profile can connect it once the Owner has set the application up.
                if (_aniList is { } aniList && (aniList.IsConnected || aniList.IsConfigured))
                {
                    page.Asset("Pages/User/Settings/AniList.entry.scss");
                    page.Asset("Framework/Components/Modal.entry.ts");
                    page.Asset("Framework/Components/Truncated.entry.ts");
                    page.Content.Add(new UiSection("settings.anilist.title", [AniListCard(aniList)]));
                }

                if (_speech is not null && texts.Can("profile.self.update"))
                {
                    page.Content.Add(new UiSection("speech.title", [new SpeechSettings(_speech, texts.Locale, "/Settings?handler=Speech",
                        antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "", _speechInput is not null ? FormError : null, _speechInput, _speechModels)]));
                }
            }
            else
            {
                page.Content.Add(new P(new Text("account.profile.required")).Attribute("role", "status"));
                page.Content.Add(new A(new Text("account.profile.title")).Attribute("href", "/Account/Profiles?returnUrl=%2FSettings"));
                if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Class("field-error").Attribute("role", "alert"));
            }

            // Home shelves in their order: dragged into place, each switched on or off, saved with the page's other changes.
            if (_layout.Count > 0)
            {
                var items = _layout.Select(shelf => new SortableItem(shelf.HomeShelfTypeId.ToString(), new Text("home.shelf." + shelf.HomeShelfTypeId), ShelfIcon(shelf.HomeShelfTypeId),
                    !shelf.IsHidden, ShelfTone(shelf.HomeShelfTypeId))).ToArray();
                var layout = new SaveHomeLayoutDataV1([.. _layout.Select(shelf => shelf.HomeShelfTypeId.ToString())], [.. _layout.Where(shelf => shelf.IsHidden).Select(shelf => shelf.HomeShelfTypeId.ToString())]);
                page.Content.Add(new UiSection("home.layout", [new Form<SaveHomeLayoutDataV1>(form =>
                {
                    form.Sortable(x => x.ShelfKeys, "home.layout", assets, items, "shownKeys", "home.shown", "home.hidden");
                    form.Save();
                }, Url.Page("/User/Settings/Settings", "HomeLayout"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, layout)]));
            }

            page.Content.Add(new UiSection("settings.account", [
                new A(new Text("settings.sessions")).Class("button").Attribute("href", "/Account/Sessions"),
                .. (texts.Can("profile.devices", "Devices") ? new[] { new A(new Text("devices.pair.title")).Class("button").Attribute("href", "/Devices/Pair") } : []),
                .. (texts.CanManageProfiles()
                    ? new[] { new A(new Text("account.profile.title")).Class("button").Attribute("href", "/Account/Profiles?returnUrl=%2FSettings") }
                    : []),
                new A(new Text("account.logout.title")).Class("button").Attribute("href", "/Account/Logout")
            ]));

            // Granted administration routes in one section.
            var administration = new List<IUiComponent>();
            if (_authentication?.Permissions.Contains("storage.read", StringComparer.Ordinal) is true)
            {
                administration.Add(new A(new Text("storage.title")).Class("button").Attribute("href", "/Admin/Storage"));
                administration.Add(new A(new Text("setup.storage.title")).Class("button").Attribute("href", "/Admin/Storage"));
            }

            if (_authentication?.Permissions.Contains("acquisition.manage", StringComparer.Ordinal) is true)
            {
                administration.Add(new A(new Text("admin.acquisition.title")).Class("button").Attribute("href", "/Admin/Acquisition"));
                administration.Add(new A(new Text("admin.profiles.title")).Class("button").Attribute("href", "/Admin/Acquisition/Profiles"));
            }

            if (_authentication?.Permissions.Contains("instance.settings.read", StringComparer.Ordinal) is true)
            {
                administration.Add(new A(new Text("admin.instance.title")).Class("button").Attribute("href", "/Admin/Instance"));
            }

            if (_authentication?.Permissions.Contains("backup.read", StringComparer.Ordinal) is true)
            {
                administration.Add(new A(new Text("admin.backup.title")).Class("button").Attribute("href", "/Admin/Backup"));
            }

            if (_authentication?.Permissions.Contains("accounts.read", StringComparer.Ordinal) is true)
            {
                administration.Add(new A(new Text("admin.accounts.title")).Class("button").Attribute("href", "/Admin/Accounts"));
            }

            if (_authentication?.Permissions.Contains("localization.read", StringComparer.Ordinal) is true)
            {
                administration.Add(new A(new Text("admin.localization.title")).Class("button").Attribute("href", "/Admin/Localization"));
            }

            if (_authentication?.Permissions.Contains("providers.read", StringComparer.Ordinal) is true)
            {
                administration.Add(new A(new Text("admin.providers.title")).Class("button").Attribute("href", "/Admin/Providers"));
            }

            if (_authentication?.Permissions.Contains("instance.settings.read", StringComparer.Ordinal) is true)
            {
                administration.Add(new A(new Text("admin.catalog.title")).Class("button").Attribute("href", "/Admin/Catalog"));
            }
            if (administration.Count > 0) page.Content.Add(new UiSection("nav.admin", administration));
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
            return RedirectToPage("/User/Settings/Settings");
        }

        public async Task<IActionResult> OnPostFavoriteRequestAsync(bool autoRequestOnFavorite)
        {
            var current = await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: HttpContext.RequestAborted);
            var profileId = current.ProfileId ?? throw new UnauthorizedAccessException("An active profile is required.");
            await updateFavoriteRequest.ExecuteAsync<UpdateResult>(new UpdateProfileParametersV1(profileId),
                new UpdateProfileFavoriteRequestDataV1(autoRequestOnFavorite), HttpContext.RequestAborted);
            return RedirectToPage("/User/Settings/Settings");
        }

        // One form for the profile's audio, subtitles, speed and autoplay.
        private IUiComponent PlaybackForm(PlaybackPreferencesV1 preferences)
        {
            var input = _playbackInput ?? new SavePlaybackPreferencesDataV1(preferences.AudioLanguage, preferences.SubtitleLanguage, preferences.SubtitleModeTypeId, preferences.Speed,
                preferences.AutoplayNext, preferences.IntroSkipModeTypeId, preferences.OutroSkipModeTypeId, preferences.AutoplayPreviews);
            IEnumerable<SelectOption> LanguageOptions(string? chosen, string emptyKey) => [new SelectOption("", texts[emptyKey]),
                .. Languages.Concat(chosen is null || Languages.Contains(chosen) ? [] : [chosen]).Select(code => new SelectOption(code, LanguageLabel.Name(code) ?? code))];
            return new Form<SavePlaybackPreferencesDataV1>(form =>
            {
                form.Select(item => item.AudioLanguage, "playback.audioLanguage", LanguageOptions(input.AudioLanguage, "playback.language.file"));
                form.Select(item => item.SubtitleModeTypeId, "playback.subtitleMode", Enum.GetValues<SubtitleMode>()
                    .Select(mode => new SelectOption(((byte)mode).ToString(CultureInfo.InvariantCulture), texts["playback.subtitleMode." + mode])));
                form.Select(item => item.SubtitleLanguage, "playback.subtitleLanguage", LanguageOptions(input.SubtitleLanguage, "playback.language.profile"));
                form.Select(item => item.Speed, "playback.speed", Speeds.Select(speed => new SelectOption(speed.ToString("0.00", CultureInfo.InvariantCulture),
                    speed.ToString("0.##", CultureInfo.CurrentCulture) + "×")));
                form.Toggle(item => item.AutoplayNext, "playback.autoplayNext");
                var skipModes = Enum.GetValues<SkipMode>().Select(mode => new SelectOption(((byte)mode).ToString(CultureInfo.InvariantCulture), texts["playback.skipMode." + mode])).ToArray();
                form.Select(item => item.IntroSkipModeTypeId, "playback.introSkipMode", skipModes);
                form.Select(item => item.OutroSkipModeTypeId, "playback.outroSkipMode", skipModes);
                form.Toggle(item => item.AutoplayPreviews, "playback.autoplayPreviews");
                form.Save();
            }, Url.Page("/User/Settings/Settings", "Playback"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, input, _playbackInput is not null ? FormError : null);
        }

        // Not connected: sign in at AniList, paste the code its PIN page shows. Connected: who it is, whether progress goes out, disconnect.
        private IUiComponent AniListCard(AniListConnectionV1 account)
        {
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var error = _savingAniList ? FormError : null;
            if (!account.IsConnected)
            {
                return new Div(
                    new A(new Icon("anilist", assets, 20), new Text("settings.anilist.open")).Class("button").Attribute("href", account.AuthorizationUrl ?? "#").Attribute("target", "_blank")
                        .Attribute("rel", "noopener noreferrer"),
                    new Form<ConnectAniListDataV1>(form =>
                    {
                        form.Password(item => item.Code, "settings.anilist.code", "off");
                        form.Submit("settings.anilist.connect");
                    }, Url.Page("/User/Settings/Settings", "AniList"), csrf, null, error)).Class("anilist-card");
            }

            // A sign-in that ran out or failed in the last pass says what to do.
            var problem = (account.IsExpired ? "anilist_token_expired" : account.FailureCode) is { } code ? "errors." + code : null;
            var items = new List<IUiComponent>
            {
                new Div(new Icon("anilist", assets, 20), new Truncated(string.Format(CultureInfo.CurrentCulture, texts["settings.anilist.connected"], account.ViewerName))).Class("anilist-card__account")
            };
            if (problem is not null)
            {
                items.Add(new Div(new Truncated(texts[problem])).Class("anilist-card__problem").Attribute("role", "status"));
            }

            items.Add(new Form<UpdateAniListPushDataV1>(form =>
            {
                form.Toggle(item => item.IsPushEnabled, "settings.anilist.push");
                form.Save();
            }, Url.Page("/User/Settings/Settings", "AniListPush"), csrf, new UpdateAniListPushDataV1(account.IsPushEnabled), error));
            items.Add(Modal.Opener(new Button(new Text("settings.anilist.disconnect")).Warn(), "anilist-disconnect"));
            items.Add(Modal.Confirm(assets, "anilist-disconnect", "settings.anilist.disconnectConfirm", "settings.anilist.disconnect", Url.Page("/User/Settings/Settings", "AniListDisconnect")!, csrf, new AniListParametersV1()));
            return new Div([.. items]).Class("anilist-card");
        }

        public async Task<IActionResult> OnPostAniListAsync(string? code)
        {
            _savingAniList = true;
            await connectAniList.ExecuteAsync<UpdateResult>(new AniListParametersV1(), new ConnectAniListDataV1(code ?? ""), HttpContext.RequestAborted);
            Hint("profile.settings.saved");
            return RedirectToPage("/User/Settings/Settings");
        }

        public async Task<IActionResult> OnPostAniListPushAsync(bool isPushEnabled)
        {
            _savingAniList = true;
            await updateAniListPush.ExecuteAsync<UpdateResult>(new AniListParametersV1(), new UpdateAniListPushDataV1(isPushEnabled), HttpContext.RequestAborted);
            return RedirectToPage("/User/Settings/Settings");
        }

        public async Task<IActionResult> OnPostAniListDisconnectAsync()
        {
            _savingAniList = true;
            await disconnectAniList.ExecuteAsync<UpdateResult>(new AniListParametersV1(), default, HttpContext.RequestAborted);
            return RedirectToPage("/User/Settings/Settings");
        }

        public async Task<IActionResult> OnPostPlaybackAsync(string? audioLanguage, string? subtitleLanguage, [BindRequired] SubtitleMode subtitleModeTypeId, [BindRequired] decimal speed, bool autoplayNext, [BindRequired] SkipMode introSkipModeTypeId, [BindRequired] SkipMode outroSkipModeTypeId, bool autoplayPreviews)
        {
            _playbackInput = new SavePlaybackPreferencesDataV1(audioLanguage, subtitleLanguage, subtitleModeTypeId, speed, autoplayNext, introSkipModeTypeId, outroSkipModeTypeId, autoplayPreviews);
            await savePlayback.ExecuteAsync<UpdateResult>(new PlaybackPreferencesParametersV1(), _playbackInput, HttpContext.RequestAborted);
            return RedirectToPage("/User/Settings/Settings");
        }

        public async Task<IActionResult> OnPostPwaAccentAsync(string? accentHex)
        {
            _pwaInput = new SavePwaAccentDataV1(accentHex ?? "");
            await savePwaAccent.ExecuteAsync<UpdateResult>(
                new SelectProfileParametersV1((await ActiveProfileAsync()).ProfileId),
                _pwaInput, HttpContext.RequestAborted);
            return RedirectToPage("/User/Settings/Settings");
        }

        public async Task<IActionResult> OnPostMediaAsync(string[]? hiddenKeys)
        {
            _savingMedia = true;
            _authentication = await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: HttpContext.RequestAborted);
            var profileId = _authentication!.ProfileId ?? throw new UnauthorizedAccessException("An active profile is required.");
            await updateMedia.ExecuteAsync<UpdateResult>(new UpdateProfileParametersV1(profileId), new UpdateProfileMediaDataV1(hiddenKeys ?? []), HttpContext.RequestAborted);
            return RedirectToPage("/User/Settings/Settings");
        }

        private static string ShelfIcon(HomeShelfType shelf) => shelf switch
        {
            HomeShelfType.ContinueWatching => "play",
            HomeShelfType.ContinueListening => "headphones",
            HomeShelfType.ContinueReading => "book",
            HomeShelfType.RecentlyAdded => "clock",
            _ => "spark"
        };

        private static UiTone ShelfTone(HomeShelfType shelf) => shelf switch
        {
            HomeShelfType.ContinueWatching => UiTone.Accent,
            HomeShelfType.ContinueListening => UiTone.Info,
            HomeShelfType.ContinueReading => UiTone.Success,
            HomeShelfType.RecentlyAdded => UiTone.Warning,
            _ => UiTone.Danger
        };

        public async Task<IActionResult> OnPostHomeLayoutAsync(string[]? shelfKeys, string[]? shownKeys)
        {
            // The whole order is saved; the shelves that are not switched on are the hidden ones.
            var order = shelfKeys ?? [];
            var shown = shownKeys ?? [];
            await saveLayout.ExecuteAsync<UpdateResult>(new HomeLayoutParametersV1(), new SaveHomeLayoutDataV1(order, [.. order.Where(key => !shown.Contains(key, StringComparer.Ordinal))]),
                HttpContext.RequestAborted);
            return RedirectToPage("/User/Settings/Settings");
        }

        public async Task<IActionResult> OnPostPasswordAsync(string? currentPassword, string? newPassword, string? newPasswordConfirmation)
        {
            _savingPassword = true;
            await changePassword.ExecuteAsync<UpdateResult>(new ChangeAccountPasswordParametersV1(),
                new ChangeAccountPasswordDataV1(currentPassword ?? "", newPassword ?? "", newPasswordConfirmation ?? ""), HttpContext.RequestAborted);
            Hint("account.password.changed");
            return RedirectToPage("/User/Settings/Settings");
        }

        public async Task<IActionResult> OnPostPinAsync(string? pin)
        {
            _savingPin = true;
            await updatePin.ExecuteAsync<UpdateResult>(await ActiveProfileAsync(), new UpdateProfilePinDataV1(pin ?? ""), HttpContext.RequestAborted);
            Hint("profile.settings.saved");
            return RedirectToPage("/User/Settings/Settings");
        }

        public async Task<IActionResult> OnPostPinRemoveAsync()
        {
            _savingPin = true;
            await removePin.ExecuteAsync<UpdateResult>(await ActiveProfileAsync(), default, HttpContext.RequestAborted);
            Hint("profile.settings.saved");
            return RedirectToPage("/User/Settings/Settings");
        }

        public async Task<IActionResult> OnPostDeleteAsync()
        {
            await deleteProfile.ExecuteAsync<UpdateResult>(await ActiveProfileAsync(), default, HttpContext.RequestAborted);
            NoHint();
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
                _pwa = await readPwaAppearance.ExecuteAsync<PwaAppearanceV1>(cancellationToken: token);
                _speech = await readSpeech.ExecuteAsync<SpeechPreferencesV1>(new ReadProfileParametersV1(profileId), token);
                if (texts.Can("profile.media.read", "Reading"))
                {
                    _speechModels = await readSpeechModels.ExecuteAsync<BaseDataList<SpeechModelV1>>(new SpeechModelsParametersV1(), token);
                }
                _media = await readMedia.ExecuteAsync<BaseDataList<ProfileMediaV1>>(new ReadProfileParametersV1(profileId), token);
                if (texts.CanPlay()) _playback = await readPlayback.ExecuteAsync<PlaybackPreferencesV1>(new PlaybackPreferencesParametersV1(), token);
                if (texts.CanSyncAniList()) _aniList = await readAniList.ExecuteAsync<AniListConnectionV1>(new AniListParametersV1(), token);
                _locales = await readLocales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: token);
                if (texts.CanArrangeHome())
                {
                    _layout = await readLayout.ExecuteAsync<BaseDataList<HomeShelfV1>>(new HomeLayoutParametersV1(), token);
                }
            }
        }

        public async Task<IActionResult> OnPostSpeechAsync([BindRequired] decimal rate, [BindRequired] decimal pitch, [BindRequired] decimal volume, long version,
            string language, SpeechProviderType providerTypeId, string? modelId, string? voiceId)
        {
            _speechInput = new SaveSpeechPreferencesDataV1(rate, pitch, volume, version, language, providerTypeId, modelId, voiceId);
            await saveSpeech.ExecuteAsync<UpdateResult>(await ActiveProfileAsync(), _speechInput, HttpContext.RequestAborted);
            return RedirectToPage("/User/Settings/Settings");
        }
    }
}
