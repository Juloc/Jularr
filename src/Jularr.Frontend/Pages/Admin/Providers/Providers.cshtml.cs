using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Account.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Provider;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Service.Admin.Account.V1;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Providers
{
    // The registry's providers as cards (state, switch, test) whose settings are inputs inside the card, saved by the page's pill; the media servers' users are mapped to profiles in a list below their cards.
    public sealed class ProvidersModel(ReadProvidersV1 providers, UpdateProviderV1 update, SaveProviderSecretV1 saveSecret, ReadPlexServerV1 readPlex, SavePlexServerV1 savePlex,
        SyncPlexServerV1 syncPlex, DisconnectPlexServerV1 disconnectPlex, ReadPlexSignInV1 readSignIn, UpdatePlexSignInV1 updateSignIn, ReadMediaServerV1 readServer, SaveMediaServerV1 saveServer, SyncMediaServerV1 syncServer,
        DisconnectMediaServerV1 disconnectServer, ReadMediaServerLinksV1 readLinks, SaveMediaServerLinkV1 saveLink, ReadProfileChoicesV1 readProfiles, FrontendAssets assets, IAntiforgery antiforgery,
        UiText texts, StartPlexServerPinV1 startPin, CompletePlexServerPinV1 completePin) : JularrPage
    {
        // Providers that take a key (IGDB adds the Twitch application id; Hardcover's token names book series, AcoustID's key untagged music, TVDB's project key episodes).
        private static readonly string[] KeyProviders = ["tmdb", "tvdb", "igdb", "anilist", "hardcover", "acoustid"];
        private static readonly string[] ServerKeys = ["plex", "jellyfin", "emby"];

        // The cards follow this order, as in the mockup: movies and series first, then books, music, games, then the servers.
        private static readonly string[] Order = ["tmdb", "tvdb", "anilist", "openlibrary", "musicbrainz", "igdb", "hardcover", "acoustid", "plex", "jellyfin", "emby"];

        private readonly Dictionary<MediaServerType, (MediaServerDetailsV1 Server, BaseDataList<MediaServerLinkV1> Links)> _servers = [];
        private BaseDataList<ProfileChoiceV1>? _profiles;
        private MediaServerType? _serverInput;
        private SaveMediaServerDataV1? _serverData;
        private PageResult<ProviderDetailsV1>? _providers;
        private PlexServerDetailsV1? _plex;
        private SavePlexServerDataV1? _plexInput;
        private PlexSignInV1? _signIn;
        private SavePlexSignInDataV1? _signInInput;
        private long? _secretFailedFor;
        private StartPlexResultV1? _pin;

        private bool IsSetup => SetupWizard.IsActive(Request);
        private bool IsServers => View == "servers" && !IsSetup;
        private string Home => SetupWizard.Address(Request, SetupStepType.Providers, "/Admin/Providers");
        private string ServersHome => IsSetup ? Home : Home + "?view=servers";

        // The chip: the providers or the media servers (every provider on one page in the setup).
        [BindProperty(SupportsGet = true, Name = "view")]
        public string? View { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title(IsServers ? "admin.mediaServers.title" : "admin.providers.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Providers/Providers.entry.scss");
            page.Asset("Framework/Components/Truncated.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            // Render only real registry rows; the media servers of Jellyfin and Emby need their modules.
            var visible = (_providers?.Items ?? []).Where(provider => MediaServerTypes.FromProviderKey(provider.Key) is null || texts.Can(null, "ProgressSync", "Library"))
                .OrderBy(provider => Rank(provider.Key)).ToList();
            if (IsSetup)
            {
                page.Content.Add(SetupWizard.Frame(HttpContext, SetupStepType.Providers, acquisition: texts.Can(null, "Acquisition")));
            }
            else
            {
                // The entry's one row, with how many providers and media servers there are; media servers show once one is set up.
                page.NoPageRow();
                var counts = new Dictionary<string, int>
                {
                    ["admin.providers.title"] = visible.Count(provider => !ServerKeys.Contains(provider.Key)),
                    ["admin.mediaServers.title"] = visible.Count(provider => ServerKeys.Contains(provider.Key))
                };
                page.Content.Add(new ListToolbar(AdminMenu.Row(texts, Request, ariaLabelKey: "admin.integrations", counts: counts)));
            }

            var shown = IsSetup ? visible : visible.Where(provider => ServerKeys.Contains(provider.Key) == IsServers).ToList();
            page.Content.Add(new CardGrid([.. shown.Select(provider => Card(provider, csrf))]).Paged(_providers?.Items.Count ?? 0, _providers?.PageSize ?? 25));
            if ((IsServers || IsSetup) && Links(csrf) is { } links) page.Content.Add(links);
            if (FormError is not null && _secretFailedFor is null && _plexInput is null && _serverInput is null && _signInInput is null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
        }

        private static int Rank(string key)
        {
            var index = Array.IndexOf(Order, key);
            return index < 0 ? Order.Length : index;
        }

        // The state says whether the provider is off, still needs its key or server, or is ready.
        private (string Key, UiTone Tone, string Icon) State(ProviderDetailsV1 provider)
        {
            if (!provider.IsEnabled) return ("admin.providers.disabled", UiTone.Neutral, "power");
            var type = MediaServerTypes.FromProviderKey(provider.Key);
            var isServer = provider.Key == "plex" || type is not null;
            var connected = provider.Key == "plex" ? _plex?.ExternalId is not null : type is { } serverType && _servers.TryGetValue(serverType, out var server) && server.Server.ExternalId is not null;
            if (isServer) return connected ? ("providers.state.connected", UiTone.Success, "check") : ("providers.state.setup", UiTone.Warning, "alert");
            return KeyProviders.Contains(provider.Key) && !provider.HasSecret ? ("providers.state.setup", UiTone.Warning, "alert") : ("admin.providers.enabled", UiTone.Success, "check");
        }

        private IUiComponent Card(ProviderDetailsV1 provider, string? csrf)
        {
            var id = provider.Id.ToString(CultureInfo.InvariantCulture);
            var (stateKey, tone, icon) = State(provider);
            var type = MediaServerTypes.FromProviderKey(provider.Key);
            var chip = new StateChip(new Text(stateKey), tone, Glyph(icon, 14));
            var toggle = Post(Home + "?providerId=" + id, csrf, [("isEnabled", provider.IsEnabled ? "false" : "true"), ("expectedIsEnabled", provider.IsEnabled ? "true" : "false")],
                new SwitchButton(provider.IsEnabled, "admin.providers.state"));
            var body = new List<IUiComponent> { new Div(chip, toggle).Class("feature-card__row") };

            // A key is never shown, only whether one is stored.
            if (KeyProviders.Contains(provider.Key))
            {
                body.Add(new Div(new Span(new Text("providers.key")).Class("feature-card__note"), provider.HasSecret ? new Tag(new Text("providers.key.saved")).Accent() : new Tag(new Text("providers.key.missing")).Warning()).Class("feature-card__row"));
            }

            // What a media server did last.
            DateTime? syncedAt = provider.Key == "plex" ? _plex?.SyncedAt : type is { } serverType && _servers.TryGetValue(serverType, out var server) ? server.Server.SyncedAt : null;
            if (ServerKeys.Contains(provider.Key) && State(provider).Key == "providers.state.connected")
            {
                // Plex also says how many of its titles are linked to Jularr's.
                var last = syncedAt is { } synced ? synced.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : texts["admin.plex.notSynced"];
                var line = provider.Key == "plex" && _plex is { } linked ? string.Format(CultureInfo.CurrentCulture, texts["admin.plex.connected"], linked.LinkedCount, last)
                    : syncedAt is null ? last : string.Format(CultureInfo.CurrentCulture, texts["providers.syncedAt"], last);
                body.Add(new Div(new Span(new ValueText(line)).Class("feature-card__note")).Class("feature-card__row"));
            }

            // The settings are inputs of the card itself: the protected key, the Plex server or the Jellyfin/Emby connection.
            if (KeyProviders.Contains(provider.Key)) body.Add(SecretForm(provider, csrf));
            if (provider.Key == "plex" && _plex is { } plex) DefinePlex(body, plex, csrf);
            if (type is { } serverKind && _servers.TryGetValue(serverKind, out var connection)) DefineMediaServer(body, serverKind, connection.Server, csrf);

            // A connected server is refreshed or disconnected here; Test reads the stored address again and keeps the stored key.
            var start = new List<IUiComponent>();
            var end = new List<IUiComponent>();
            if (State(provider).Key == "providers.state.connected")
            {
                var action = provider.Key == "plex" ? Home + "?handler=Plex" : Home + "?mediaServerTypeId=" + type + "&handler=MediaServer";
                var isPlex = provider.Key == "plex";
                start.Add(Post(action + "Sync", csrf, [], new Button(new Text(isPlex ? "admin.plex.sync" : "admin.mediaServer.sync")).Compact().Soft().Submit()));
                start.Add(Post(action + "Disconnect", csrf, [], new Button(new Text(isPlex ? "admin.plex.disconnect" : "admin.mediaServer.disconnect")).Compact().Warn().Submit()));
                if (provider.IsEnabled) end.Add(Post(action + "Test", csrf, [], new Button(Glyph("sync", 14), new Text("providers.test")).Compact().Soft().Submit()));
            }

            var icon2 = provider.Key == "plex" ? "plex" : ServerKeys.Contains(provider.Key) ? "server" : "globe";
            var card = new FeatureCard(new IconTile(Glyph(icon2, 18), UiTone.Accent), texts.Has("provider.name." + provider.Key) ? texts["provider.name." + provider.Key] : provider.Key,
                texts.Has("provider.hint." + provider.Key) ? texts["provider.hint." + provider.Key] : "", null, body, start, end, "provider-" + provider.Key);
            return ServerKeys.Contains(provider.Key) ? card.Wide() : card;
        }

        // The protected key (IGDB adds the Twitch application id, AniList the application profiles sign in with); it is never written back.
        private IUiComponent SecretForm(ProviderDetailsV1 provider, string? csrf) =>
            new Form<SaveProviderSecretDataV1>(form =>
            {
                if (provider.Key is "igdb" or "anilist") form.Text(item => item.ClientId, "admin.providers.clientId");
                form.Password(item => item.Secret, provider.Key is "igdb" or "anilist" ? "admin.providers.clientSecret" : "admin.providers.secret", "new-password");
                form.Save();
            }, Home + "?providerId=" + provider.Id.ToString(CultureInfo.InvariantCulture) + "&handler=Secret", csrf, new SaveProviderSecretDataV1(null), _secretFailedFor == provider.Id ? FormError : null);

        // The Plex server: address and token side by side, the plex.tv connection beside them, then who signs in and registers with it.
        private void DefinePlex(List<IUiComponent> body, PlexServerDetailsV1 plex, string? csrf)
        {
            body.Add(new Form<SavePlexServerDataV1>(form =>
            {
                form.Text(item => item.BaseUrl, "admin.plex.baseUrl");
                form.Password(item => item.Token, "admin.plex.token", "new-password");
                form.Action(new Button(new Text("admin.plex.pin.start")).Compact().Soft().Attribute("formaction", Home + "?handler=PlexPin").Attribute("formnovalidate", "formnovalidate")
                    .Attribute("data-no-draft", ""));
                form.Save();
            }, Home + "?handler=Plex", csrf, _plexInput ?? new SavePlexServerDataV1(plex.BaseUrl ?? "", null), _plexInput is null ? null : FormError));
            if (_pin is { } pin)
            {
                body.Add(new Div(new A(new Text("account.plex.authorize")).Attribute("href", pin.AuthorizationUrl).Attribute("target", "_blank").Attribute("rel", "noopener noreferrer"),
                    new Form<CompletePlexParametersV1>(form =>
                    {
                        form.Hidden(item => item.AuthChallengePublicId);
                        form.Submit("account.plex.complete");
                    }, Home + "?handler=PlexPinComplete", csrf, new CompletePlexParametersV1(pin.AuthChallengePublicId), FormError)).Class("feature-card__row"));
            }

            // The libraries come from the connected server.
            if (plex.ExternalId is not null && _signIn is { } signIn)
            {
                body.Add(new Form<SavePlexSignInDataV1>(form =>
                {
                    form.Toggle(item => item.SignIn, "admin.plex.signIn");
                    form.Toggle(item => item.Registration, "admin.plex.registration");
                    form.Toggle(item => item.RegistrationApproval, "admin.plex.registrationApproval");
                    if (signIn.SectionKeys.Length > 0)
                    {
                        form.Choices(item => item.RegistrationSections, "admin.plex.registrationSections", signIn.SectionKeys.Zip(signIn.SectionTitles, (key, title) => new SelectOption(key, title)));
                    }

                    form.Save();
                }, Home + "?handler=PlexSignIn", csrf, _signInInput ?? new SavePlexSignInDataV1(signIn.SignIn, signIn.Registration, signIn.RegistrationApproval, signIn.RegistrationSections),
                    _signInInput is null ? null : FormError));
            }
        }

        private void DefineMediaServer(List<IUiComponent> body, MediaServerType type, MediaServerDetailsV1 server, string? csrf)
        {
            body.Add(new Form<SaveMediaServerDataV1>(form =>
            {
                form.Text(item => item.BaseUrl, "admin.mediaServer.baseUrl", help: new FieldHelp(Placeholder: "hint.mediaServer.baseUrl.placeholder"));
                form.Password(item => item.ApiKey, "admin.mediaServer.apiKey", "new-password");
                form.Save();
            }, Home + "?mediaServerTypeId=" + type + "&handler=MediaServer", csrf, _serverInput == type ? _serverData : new SaveMediaServerDataV1(server.BaseUrl ?? "", null), _serverInput == type ? FormError : null));
        }

        // Which Jularr profile each server user shares progress with: the user, the server, the state and the profile to confirm.
        private IUiComponent? Links(string? csrf)
        {
            var options = (_profiles?.AsEnumerable() ?? []).Select(profile => new SelectOption(profile.ProfileId.ToString(CultureInfo.InvariantCulture), profile.DisplayName + " (" + profile.AccountName + ")"))
                .Prepend(new SelectOption("", texts["admin.mediaServer.noProfile"])).ToArray();
            var rows = new List<ListRow>();
            foreach (var (type, (_, links)) in _servers)
            {
                var server = texts["provider.name." + type.ProviderKey()];
                foreach (var link in links)
                {
                    var (stateKey, tone, icon) = link.ProfileName is null ? ("admin.mediaServer.state.unlinked", UiTone.Neutral, "link") : link.IsConfirmed ? ("admin.mediaServer.state.linked", UiTone.Success, "check") : ("admin.mediaServer.state.proposed", UiTone.Info, "clock");
                    var identity = new ListIdentity(new IconTile(Glyph("user", 18), UiTone.Accent), new ValueText(link.ExternalName), new ValueText(server));
                    var profile = link.ProfileName is null ? new Span(new ValueText("–")).Class("list-value list-value--dim") : new Span(new ValueText(link.ProfileName)).Class("list-value");
                    var form = new Form<SaveMediaServerLinkDataV1>(item =>
                    {
                        item.Select(data => data.ProfileId, "admin.mediaServer.profile", options);
                        item.Submit(new Button(new Text("admin.mediaServer.confirm")).Compact().Soft());
                    }, Home + "?mediaServerTypeId=" + type + "&handler=MediaServerLink&linkId=" + link.Id.ToString(CultureInfo.InvariantCulture), csrf, new SaveMediaServerLinkDataV1(link.ProfileId));
                    rows.Add(new ListRow([identity, new StateChip(new Text(stateKey), tone, Glyph(icon, 14)), profile, new Div(form).Class("list-actions")],
                        [ListPanel.Fact("admin.mediaServer.profile", link.ProfileName is null ? new ValueText("–") : new ValueText(link.ProfileName))]));
                }
            }

            if (rows.Count == 0) return null;
            var columns = new List<ListColumn> { new("admin.mediaServer.user", ListRole.Main), new("table.status", ListRole.Status), new("admin.mediaServer.profile", ListRole.Data, 1), new("common.actions", ListRole.Actions) };
            return new ListPanel("admin.mediaServer.links", columns, rows).Modifier("providers-links");
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(long providerId, [BindRequired] bool isEnabled, [BindRequired] bool expectedIsEnabled)
        {
            await update.ExecuteAsync<UpdateResult>(new UpdateProviderParametersV1(providerId), new UpdateProviderDataV1(isEnabled, expectedIsEnabled), HttpContext.RequestAborted);
            return LocalRedirect(await ProviderAddressAsync(providerId));
        }

        public async Task<IActionResult> OnPostSecretAsync(long providerId, string? secret, string? clientId)
        {
            _secretFailedFor = providerId;
            await saveSecret.ExecuteAsync<UpdateResult>(new UpdateProviderParametersV1(providerId), new SaveProviderSecretDataV1(string.IsNullOrWhiteSpace(secret) ? null : secret,
                string.IsNullOrWhiteSpace(clientId) ? null : clientId), HttpContext.RequestAborted);
            return LocalRedirect(await ProviderAddressAsync(providerId));
        }

        public async Task<IActionResult> OnPostPlexPinAsync(string? baseUrl)
        {
            _plexInput = new SavePlexServerDataV1(baseUrl ?? "", null);
            _pin = await startPin.ExecuteAsync<StartPlexResultV1>(new PlexServerParametersV1(), _plexInput, HttpContext.RequestAborted);
            await ServiceApi.ApplyAuthenticationAsync(HttpContext, _pin, antiforgery);
            TempData["PlexServerPin"] = JsonSerializer.Serialize(_pin);
            NoHint();
            return LocalRedirect(ServersHome);
        }

        public async Task<IActionResult> OnPostPlexPinCompleteAsync(Guid authChallengePublicId)
        {
            var result = await completePin.ExecuteAsync<UpdateResult>(new CompletePlexParametersV1(authChallengePublicId), default, HttpContext.RequestAborted);
            if (result.Success)
            {
                TempData.Remove("PlexServerPin");
                Response.Cookies.Delete(ServiceApi.AuthenticationNonceCookieName);
            }
            return LocalRedirect(ServersHome);
        }

        public async Task<IActionResult> OnPostPlexAsync(string? baseUrl, string? token)
        {
            _plexInput = new SavePlexServerDataV1(baseUrl ?? "", string.IsNullOrWhiteSpace(token) ? null : token);
            var test = await savePlex.ExecuteAsync<PlexServerTestV1>(new PlexServerParametersV1(), _plexInput, HttpContext.RequestAborted);
            HintText(string.Format(CultureInfo.InvariantCulture, texts["admin.plex.tested"], test.Version ?? "?", string.Join(", ", test.Sections)));
            return LocalRedirect(ServersHome);
        }

        // The stored address and key answer again.
        public async Task<IActionResult> OnPostPlexTestAsync()
        {
            var plex = await readPlex.ExecuteAsync<PlexServerDetailsV1>(new PlexServerParametersV1(), HttpContext.RequestAborted);
            var test = await savePlex.ExecuteAsync<PlexServerTestV1>(new PlexServerParametersV1(), new SavePlexServerDataV1(plex.BaseUrl ?? "", null), HttpContext.RequestAborted);
            HintText(string.Format(CultureInfo.InvariantCulture, texts["admin.plex.tested"], test.Version ?? "?", string.Join(", ", test.Sections)));
            return LocalRedirect(ServersHome);
        }

        public async Task<IActionResult> OnPostPlexSignInAsync(bool signIn, bool registration, bool registrationApproval, string[]? registrationSections)
        {
            _signInInput = new SavePlexSignInDataV1(signIn, registration, registrationApproval, registrationSections);
            await updateSignIn.ExecuteAsync<UpdateResult>(new PlexServerParametersV1(), _signInInput, HttpContext.RequestAborted);
            return LocalRedirect(ServersHome);
        }

        public async Task<IActionResult> OnPostPlexSyncAsync()
        {
            var sync = await syncPlex.ExecuteAsync<PlexSyncResultV1>(new PlexServerParametersV1(), default, HttpContext.RequestAborted);
            HintText(string.Format(CultureInfo.InvariantCulture, texts["admin.plex.synced"], sync.LinkedCount, sync.CreatedCount));
            return LocalRedirect(ServersHome);
        }

        public async Task<IActionResult> OnPostPlexDisconnectAsync()
        {
            await disconnectPlex.ExecuteAsync<UpdateResult>(new PlexServerParametersV1(), default, HttpContext.RequestAborted);
            return LocalRedirect(ServersHome);
        }

        public async Task<IActionResult> OnPostMediaServerAsync(MediaServerType mediaServerTypeId, string? baseUrl, string? apiKey)
        {
            _serverInput = mediaServerTypeId;
            _serverData = new SaveMediaServerDataV1(baseUrl ?? "", apiKey);
            var test = await saveServer.ExecuteAsync<MediaServerTestV1>(new MediaServerParametersV1(mediaServerTypeId), _serverData, HttpContext.RequestAborted);
            HintText(string.Format(CultureInfo.InvariantCulture, texts["admin.mediaServer.tested"], test.Name, test.Version ?? "", test.UserCount));
            return LocalRedirect(ServersHome);
        }

        // The stored address and key answer again.
        public async Task<IActionResult> OnPostMediaServerTestAsync(MediaServerType mediaServerTypeId)
        {
            var parameters = new MediaServerParametersV1(mediaServerTypeId);
            var server = await readServer.ExecuteAsync<MediaServerDetailsV1>(parameters, HttpContext.RequestAborted);
            var test = await saveServer.ExecuteAsync<MediaServerTestV1>(parameters, new SaveMediaServerDataV1(server.BaseUrl ?? "", null), HttpContext.RequestAborted);
            HintText(string.Format(CultureInfo.InvariantCulture, texts["admin.mediaServer.tested"], test.Name, test.Version ?? "", test.UserCount));
            return LocalRedirect(ServersHome);
        }

        public async Task<IActionResult> OnPostMediaServerSyncAsync(MediaServerType mediaServerTypeId)
        {
            var sync = await syncServer.ExecuteAsync<MediaServerSyncResultV1>(new MediaServerParametersV1(mediaServerTypeId), default, HttpContext.RequestAborted);
            HintText(string.Format(CultureInfo.InvariantCulture, texts["admin.mediaServer.synced"], sync.LocalCount, sync.RemoteCount));
            return LocalRedirect(ServersHome);
        }

        public async Task<IActionResult> OnPostMediaServerDisconnectAsync(MediaServerType mediaServerTypeId)
        {
            await disconnectServer.ExecuteAsync<UpdateResult>(new MediaServerParametersV1(mediaServerTypeId), default, HttpContext.RequestAborted);
            return LocalRedirect(ServersHome);
        }

        public async Task<IActionResult> OnPostMediaServerLinkAsync(MediaServerType mediaServerTypeId, long linkId, long? profileId)
        {
            await saveLink.ExecuteAsync<UpdateResult>(new MediaServerLinkParametersV1(mediaServerTypeId, linkId), new SaveMediaServerLinkDataV1(profileId), HttpContext.RequestAborted);
            return LocalRedirect(ServersHome);
        }

        protected override async Task LoadDataAsync()
        {
            if (TempData.Peek("PlexServerPin") is string pin) _pin = JsonSerializer.Deserialize<StartPlexResultV1>(pin);
            _providers = await providers.ExecuteAsync<PageResult<ProviderDetailsV1>>(new ReadProvidersParametersV1(PageNumber), HttpContext.RequestAborted);
            _plex = await readPlex.ExecuteAsync<PlexServerDetailsV1>(new PlexServerParametersV1(), HttpContext.RequestAborted);
            if (_plex.ExternalId is not null)
            {
                _signIn = await readSignIn.ExecuteAsync<PlexSignInV1>(new PlexServerParametersV1(), HttpContext.RequestAborted);
            }
            if (!texts.Can(null, "ProgressSync", "Library"))
            {
                return;
            }

            foreach (var type in Enum.GetValues<MediaServerType>())
            {
                var parameters = new MediaServerParametersV1(type);
                _servers[type] = (await readServer.ExecuteAsync<MediaServerDetailsV1>(parameters, HttpContext.RequestAborted),
                    await readLinks.ExecuteAsync<BaseDataList<MediaServerLinkV1>>(parameters, HttpContext.RequestAborted));
            }

            if (_servers.Values.Any(server => server.Links.Count > 0))
            {
                _profiles = await readProfiles.ExecuteAsync<BaseDataList<ProfileChoiceV1>>(new ReadProfileChoicesParametersV1(), HttpContext.RequestAborted);
            }
        }

        // A provider's own action answers with the chip its card is on, so the card swaps in place.
        private async Task<string> ProviderAddressAsync(long id)
        {
            var key = _providers?.Items.FirstOrDefault(provider => provider.Id == id)?.Key
                ?? (await providers.ExecuteAsync<PageResult<ProviderDetailsV1>>(new ReadProvidersParametersV1(1, 100), HttpContext.RequestAborted)).Items.FirstOrDefault(provider => provider.Id == id)?.Key;
            return key is not null && ServerKeys.Contains(key) ? ServersHome : Home;
        }

        private static IUiComponent Post(string action, string? csrf, (string Name, string Value)[] hidden, IUiComponent control) => new PostForm(action, csrf, hidden, control);

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);
    }
}
