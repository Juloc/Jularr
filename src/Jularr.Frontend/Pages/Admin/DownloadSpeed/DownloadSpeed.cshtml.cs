using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Live;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.DownloadSpeed
{
    // The speed limit of every client that can be held back, each in its card beside the speed it has now; the page's pill saves them. The Downloads page keeps its quick choice of the same limit.
    public sealed class DownloadSpeedModel(ReadDownloadClientsV1 readClients, ReadDownloadSpeedLimitV1 readLimit, SetDownloadSpeedLimitV1 setLimit, FrontendAssets assets, IAntiforgery antiforgery,
        UiText texts) : JularrPage
    {
        private IReadOnlyList<DownloadClientV1> _clients = [];
        private readonly Dictionary<string, DownloadSpeedLimitV1?> _limits = [];
        private (string Key, SetDownloadSpeedLimitDataV1 Input)? _failed;

        protected override void Define(Page page)
        {
            page.Title("downloads.speedPage.title");
            page.Navigation("nav.admin");
            page.Asset("Framework/Components/LiveProgress.entry.ts");
            page.Asset("Pages/Admin/DownloadSpeed/DownloadSpeed.entry.scss");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            page.Content.Add(new H1(new Text("downloads.speedPage.title")).Class("download-speed__title"));
            if (_clients.Count == 0)
            {
                page.Content.Add(new Div(new Div(new IconTile(Glyph("download", 22), UiTone.Neutral), new P(new Text("downloads.speedPage.none")).Attribute("role", "status")).Class("list-empty")).Class("list"));
                return;
            }

            // The limits of the mockup's speed panel: a row per client with the speed it has now and its limit.
            page.Content.Add(new Section(new H2(new Text("downloads.speedPage.limits")), new Div([.. _clients.Select(client => Row(client, csrf))]).Class("download-speed__rows"))
                .Class("download-speed"));
            if (FormError is not null && _failed is null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
        }

        // One client: its name, what it downloads at now (kept current by the stream) and its limit; a client that does not answer says so instead of a limit.
        private IUiComponent Row(DownloadClientV1 client, string? csrf)
        {
            var key = ReadDownloadClientsV1.Key(client.DownloadClientKindId, client.AcquisitionConnectionId);
            var figures = LiveEntities.Downloads(texts, [client]).Text;
            var speed = new Span(new ValueText(figures.TryGetValue("speed", out var text) ? text : "")).Class("download-speed__now")
                .Attribute("data-live", LiveKeys.Downloads(key)).Attribute("data-live-text", "speed");
            IUiComponent limit;
            if (_limits.TryGetValue(key, out var current) && current is not null)
            {
                var input = _failed is { } failed && failed.Key == key ? failed.Input : new SetDownloadSpeedLimitDataV1(current.MaxBytesPerSecond);
                limit = new Form<SetDownloadSpeedLimitDataV1>(form =>
                {
                    form.ByteSize(item => item.MaxBytesPerSecond, "downloads.speedPage.limit");
                    form.Save();
                }, Address(client), csrf, input, _failed?.Key == key ? FormError : null);
            }
            else
            {
                limit = new P(new Text("downloads.speedPage.unreachable")).Class("download-speed__note");
            }

            var native = client.AcquisitionConnectionId is null;
            var name = new Div(new Strong(native ? new Text("downloads.client." + client.DownloadClientKindId) : new ValueText(client.Name)),
                native ? new Span() : new Span(new Text("acquisition.connection.type." + client.DownloadClientKindId)).Class("download-speed__kind")).Class("download-speed__name");
            return new Div(name, speed, limit).Class("download-speed__row");
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        // An empty limit holds the client back no more.
        public async Task<IActionResult> OnPostLimitAsync([BindRequired] DownloadClientKind client, long? connectionId, long? maxBytesPerSecond)
        {
            var input = new SetDownloadSpeedLimitDataV1(maxBytesPerSecond);
            _failed = (ReadDownloadClientsV1.Key(client, connectionId), input);
            await setLimit.ExecuteAsync<UpdateResult>(new DownloadClientParametersV1(client, connectionId), input, HttpContext.RequestAborted);
            return LocalRedirect("/Admin/Downloads/Speed");
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            var clients = await readClients.ExecuteAsync<BaseDataList<DownloadClientV1>>(new ReadDownloadClientsParametersV1(), ct);
            _clients = [.. clients.Where(client => DownloadClientKinds.LimitsSpeed(client.DownloadClientKindId) && (client.DownloadClientKindId != DownloadClientKind.Torrent || texts.Can(null, "Torrent")))];
            foreach (var client in _clients)
            {
                try
                {
                    _limits[ReadDownloadClientsV1.Key(client.DownloadClientKindId, client.AcquisitionConnectionId)] =
                        await readLimit.ExecuteAsync<DownloadSpeedLimitV1>(new DownloadClientParametersV1(client.DownloadClientKindId, client.AcquisitionConnectionId), ct);
                }
                catch (ServiceUnavailableException)
                {
                    _limits[ReadDownloadClientsV1.Key(client.DownloadClientKindId, client.AcquisitionConnectionId)] = null;
                }
            }
        }

        private static string Address(DownloadClientV1 client) => "/Admin/Downloads/Speed?handler=Limit&client=" + client.DownloadClientKindId
            + (client.AcquisitionConnectionId is { } id ? "&connectionId=" + id.ToString(CultureInfo.InvariantCulture) : "");

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);
    }
}
