using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Account.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Provider;
using Jularr.Backend.Service.Admin.Account.V1;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Providers
{
    public sealed class ProvidersModel(ReadProvidersV1 providers, UpdateProviderV1 update, SaveProviderSecretV1 saveSecret, ReadPlexServerV1 readPlex, SavePlexServerV1 savePlex,
        SyncPlexServerV1 syncPlex, DisconnectPlexServerV1 disconnectPlex, ReadMediaServerV1 readServer, SaveMediaServerV1 saveServer, SyncMediaServerV1 syncServer,
        DisconnectMediaServerV1 disconnectServer, ReadMediaServerLinksV1 readLinks, SaveMediaServerLinkV1 saveLink, ReadProfileChoicesV1 readProfiles, IAntiforgery antiforgery,
        UiText texts) : JularrPage
    {
        private readonly Dictionary<MediaServerType, (MediaServerDetailsV1 Server, BaseDataList<MediaServerLinkV1> Links)> _servers = [];
        private BaseDataList<ProfileChoiceV1>? _profiles;
        private MediaServerType? _serverInput;
        private SaveMediaServerDataV1? _serverData;
        private PageResult<ProviderDetailsV1>? _providers;
        private PlexServerDetailsV1? _plex;
        private SavePlexServerDataV1? _plexInput;
        private UpdateProviderDataV1? _input;
        private long? _providerId;

        private string Home => SetupWizard.Address(Request, SetupStepType.Providers, "/Admin/Providers");

        protected override void Define(Page page)
        {
            page.Title("admin.providers.title");
            if (SetupWizard.IsActive(Request)) page.Content.Add(SetupWizard.Frame(SetupStepType.Providers, antiforgery.GetAndStoreTokens(HttpContext).RequestToken));
            page.Navigation("nav.admin");
            page.Content.Add(new P(new Text("admin.providers.scope")));

            var options = new[] { new SelectOption("True", texts["admin.providers.enabled"]), new SelectOption("False", texts["admin.providers.disabled"]) };

            // Render only real registry rows.
            foreach (var provider in (_providers?.Items ?? []).Where(provider => MediaServerTypes.FromProviderKey(provider.Key) is null || texts.Can(null, "ProgressSync", "Library")))
            {
                page.Content.Add(new P(new ValueText(provider.Key)));
                page.Content.Add(new Form<UpdateProviderDataV1>(form =>
                {
                    form.Hidden(item => item.ExpectedIsEnabled);
                    form.Select(item => item.IsEnabled, "admin.providers.state", options);
                    form.Submit("common.save");
                }, Home + "?providerId=" + provider.Id.ToString(CultureInfo.InvariantCulture), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    _providerId == provider.Id ? _input : new UpdateProviderDataV1(provider.IsEnabled, provider.IsEnabled), _providerId == provider.Id ? FormError : null));

                // Metadata providers need a key; it is never shown again. IGDB adds the Twitch application id.
                if (provider.Key is "tmdb" or "igdb")
                {
                    page.Content.Add(new P(new Text(provider.HasSecret ? "admin.providers.secretSet" : "admin.providers.secretMissing")));
                    page.Content.Add(new Form<SaveProviderSecretDataV1>(form =>
                    {
                        if (provider.Key == "igdb")
                        {
                            form.Text(item => item.ClientId, "admin.providers.clientId");
                        }

                        form.Password(item => item.Secret, provider.Key == "igdb" ? "admin.providers.clientSecret" : "admin.providers.secret", "new-password");
                        form.Submit("common.save");
                    }, Home + "?providerId=" + provider.Id.ToString(CultureInfo.InvariantCulture) + "&handler=Secret", antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                        new SaveProviderSecretDataV1(null)));
                }

                // The owner's Plex server: availability, refresh after import and links.
                if (provider.Key == "plex" && _plex is { } plex)
                {
                    DefinePlex(page, plex);
                }

                // Jellyfin and Emby users share their progress with the profile an administrator confirmed.
                if (MediaServerTypes.FromProviderKey(provider.Key) is { } type && _servers.TryGetValue(type, out var server))
                {
                    DefineMediaServer(page, type, server.Server, server.Links);
                }
            }

            if (_providers?.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", Home + "?pageNumber=" + (_providers.Page - 1)));
            if (_providers is { } && _providers.Items.Count == _providers.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", Home + "?pageNumber=" + (_providers.Page + 1)));
            if (FormError is not null && _providerId is null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        private void DefinePlex(Page page, PlexServerDetailsV1 plex)
        {
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            page.Content.Add(new P(plex.ExternalId is null
                ? new Text("admin.plex.notConnected")
                : new ValueText(string.Format(CultureInfo.InvariantCulture, texts["admin.plex.connected"], plex.LinkedCount,
                    plex.SyncedAt?.ToString("u", CultureInfo.InvariantCulture) ?? texts["admin.plex.notSynced"]))));
            if (TempData["PlexResult"] is string result) page.Content.Add(new P(new ValueText(result)).Attribute("role", "status"));
            page.Content.Add(new Form<SavePlexServerDataV1>(form =>
            {
                form.Text(item => item.BaseUrl, "admin.plex.baseUrl");
                form.Password(item => item.Token, "admin.plex.token", "new-password");
                form.Submit(plex.ExternalId is null ? "admin.plex.connect" : "common.save");
            }, Home + "?handler=Plex", csrf, _plexInput ?? new SavePlexServerDataV1(plex.BaseUrl ?? "", null), _plexInput is null ? null : FormError));
            if (plex.ExternalId is not null)
            {
                page.Content.Add(new Form<PlexServerParametersV1>(form => form.Submit("admin.plex.sync"), Home + "?handler=PlexSync", csrf, new PlexServerParametersV1()));
                page.Content.Add(new Form<PlexServerParametersV1>(form => form.Submit("admin.plex.disconnect"), Home + "?handler=PlexDisconnect", csrf, new PlexServerParametersV1()));
            }
        }

        private void DefineMediaServer(Page page, MediaServerType type, MediaServerDetailsV1 server, BaseDataList<MediaServerLinkV1> links)
        {
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var action = Home + "?mediaServerTypeId=" + type + "&handler=";
            page.Content.Add(new P(server.ExternalId is null
                ? new Text("admin.mediaServer.notConnected")
                : new ValueText(string.Format(CultureInfo.InvariantCulture, texts["admin.mediaServer.connected"],
                    server.SyncedAt?.ToString("u", CultureInfo.InvariantCulture) ?? texts["admin.plex.notSynced"]))));
            if (TempData["MediaServerResult" + type] is string result) page.Content.Add(new P(new ValueText(result)).Attribute("role", "status"));
            page.Content.Add(new Form<SaveMediaServerDataV1>(form =>
            {
                form.Text(item => item.BaseUrl, "admin.mediaServer.baseUrl");
                form.Password(item => item.ApiKey, "admin.mediaServer.apiKey", "new-password");
                form.Submit(server.ExternalId is null ? "admin.plex.connect" : "common.save");
            }, action + "MediaServer", csrf, _serverInput == type ? _serverData : new SaveMediaServerDataV1(server.BaseUrl ?? "", null), _serverInput == type ? FormError : null));
            if (server.ExternalId is null)
            {
                return;
            }

            page.Content.Add(new Form<MediaServerParametersV1>(form => form.Submit("admin.mediaServer.sync"), action + "MediaServerSync", csrf, new MediaServerParametersV1(type)));
            page.Content.Add(new Form<MediaServerParametersV1>(form => form.Submit("admin.mediaServer.disconnect"), action + "MediaServerDisconnect", csrf, new MediaServerParametersV1(type)));
            var options = (_profiles?.AsEnumerable() ?? []).Select(profile => new SelectOption(profile.ProfileId.ToString(CultureInfo.InvariantCulture), profile.DisplayName + " (" + profile.AccountName + ")"))
                .Prepend(new SelectOption("", texts["admin.mediaServer.noProfile"])).ToArray();
            foreach (var link in links)
            {
                var state = link.ProfileName is null ? "admin.mediaServer.unlinked" : link.IsConfirmed ? "admin.mediaServer.linked" : "admin.mediaServer.proposed";
                page.Content.Add(new P(new ValueText(string.Format(CultureInfo.InvariantCulture, texts[state], link.ExternalName, link.ProfileName))));
                page.Content.Add(new Form<SaveMediaServerLinkDataV1>(form =>
                {
                    form.Select(item => item.ProfileId, "admin.mediaServer.profile", options);
                    form.Submit("admin.mediaServer.confirm");
                }, action + "MediaServerLink&linkId=" + link.Id.ToString(CultureInfo.InvariantCulture), csrf, new SaveMediaServerLinkDataV1(link.ProfileId)));
            }
        }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(long providerId, [BindRequired] bool isEnabled, [BindRequired] bool expectedIsEnabled)
        {
            _providerId = providerId;
            _input = new UpdateProviderDataV1(isEnabled, expectedIsEnabled);
            await update.ExecuteAsync<UpdateResult>(new UpdateProviderParametersV1(providerId), _input, HttpContext.RequestAborted);
            return LocalRedirect(Home);
        }
        public async Task<IActionResult> OnPostSecretAsync(long providerId, string? secret, string? clientId)
        {
            _providerId = providerId;
            await saveSecret.ExecuteAsync<UpdateResult>(new UpdateProviderParametersV1(providerId), new SaveProviderSecretDataV1(string.IsNullOrWhiteSpace(secret) ? null : secret,
                string.IsNullOrWhiteSpace(clientId) ? null : clientId), HttpContext.RequestAborted);
            return LocalRedirect(Home);
        }

        public async Task<IActionResult> OnPostPlexAsync(string? baseUrl, string? token)
        {
            _plexInput = new SavePlexServerDataV1(baseUrl ?? "", string.IsNullOrWhiteSpace(token) ? null : token);
            var test = await savePlex.ExecuteAsync<PlexServerTestV1>(new PlexServerParametersV1(), _plexInput, HttpContext.RequestAborted);
            TempData["PlexResult"] = string.Format(CultureInfo.InvariantCulture, texts["admin.plex.tested"], test.Version ?? "?", string.Join(", ", test.Sections));
            return LocalRedirect(Home);
        }

        public async Task<IActionResult> OnPostPlexSyncAsync()
        {
            var sync = await syncPlex.ExecuteAsync<PlexSyncResultV1>(new PlexServerParametersV1(), default, HttpContext.RequestAborted);
            TempData["PlexResult"] = string.Format(CultureInfo.InvariantCulture, texts["admin.plex.synced"], sync.LinkedCount, sync.CreatedCount);
            return LocalRedirect(Home);
        }

        public async Task<IActionResult> OnPostPlexDisconnectAsync()
        {
            await disconnectPlex.ExecuteAsync<UpdateResult>(new PlexServerParametersV1(), default, HttpContext.RequestAborted);
            return LocalRedirect(Home);
        }

        public async Task<IActionResult> OnPostMediaServerAsync(MediaServerType mediaServerTypeId, string? baseUrl, string? apiKey)
        {
            _serverInput = mediaServerTypeId;
            _serverData = new SaveMediaServerDataV1(baseUrl ?? "", apiKey);
            var test = await saveServer.ExecuteAsync<MediaServerTestV1>(new MediaServerParametersV1(mediaServerTypeId), _serverData, HttpContext.RequestAborted);
            TempData["MediaServerResult" + mediaServerTypeId] = string.Format(CultureInfo.InvariantCulture, texts["admin.mediaServer.tested"], test.Name, test.Version ?? "", test.UserCount);
            return LocalRedirect(Home);
        }

        public async Task<IActionResult> OnPostMediaServerSyncAsync(MediaServerType mediaServerTypeId)
        {
            var sync = await syncServer.ExecuteAsync<MediaServerSyncResultV1>(new MediaServerParametersV1(mediaServerTypeId), default, HttpContext.RequestAborted);
            TempData["MediaServerResult" + mediaServerTypeId] = string.Format(CultureInfo.InvariantCulture, texts["admin.mediaServer.synced"], sync.LocalCount, sync.RemoteCount);
            return LocalRedirect(Home);
        }

        public async Task<IActionResult> OnPostMediaServerDisconnectAsync(MediaServerType mediaServerTypeId)
        {
            await disconnectServer.ExecuteAsync<UpdateResult>(new MediaServerParametersV1(mediaServerTypeId), default, HttpContext.RequestAborted);
            return LocalRedirect(Home);
        }

        public async Task<IActionResult> OnPostMediaServerLinkAsync(MediaServerType mediaServerTypeId, long linkId, long? profileId)
        {
            await saveLink.ExecuteAsync<UpdateResult>(new MediaServerLinkParametersV1(mediaServerTypeId, linkId), new SaveMediaServerLinkDataV1(profileId), HttpContext.RequestAborted);
            return LocalRedirect(Home);
        }

        protected override async Task LoadDataAsync()
        {
            _providers = await providers.ExecuteAsync<PageResult<ProviderDetailsV1>>(new ReadProvidersParametersV1(PageNumber), HttpContext.RequestAborted);
            _plex = await readPlex.ExecuteAsync<PlexServerDetailsV1>(new PlexServerParametersV1(), HttpContext.RequestAborted);
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
    }
}
