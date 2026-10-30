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
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            if (_clients.Count == 0)
            {
                page.Content.Add(new Div(new Div(new IconTile(Glyph("download", 22), UiTone.Neutral), new P(new Text("downloads.speedPage.none")).Attribute("role", "status")).Class("list-empty")).Class("list"));
                return;
            }

            page.Content.Add(new CardGrid([.. _clients.Select(client => Card(client, csrf))]));
            if (FormError is not null && _failed is null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
        }

        // One client: what it downloads at now (kept current by the stream) and its limit; a client that does not answer shows why instead of a limit.
        private IUiComponent Card(DownloadClientV1 client, string? csrf)
        {
            var key = ReadDownloadClientsV1.Key(client.DownloadClientKindId, client.AcquisitionConnectionId);
            var figures = LiveEntities.Downloads(texts, [client]).Text;
            var speed = new Span(new ValueText(figures.TryGetValue("speed", out var text) ? text : "")).Class("list-value")
                .Attribute("data-live", LiveKeys.Downloads(key)).Attribute("data-live-text", "speed");
            var body = new List<IUiComponent> { new FactList([("downloads.speed", speed)]) };
            if (_limits.TryGetValue(key, out var limit) && limit is not null)
            {
                var input = _failed is { } failed && failed.Key == key ? failed.Input : new SetDownloadSpeedLimitDataV1(limit.MaxBytesPerSecond);
                body.Add(new Form<SetDownloadSpeedLimitDataV1>(form =>
                {
                    form.ByteSize(item => item.MaxBytesPerSecond, "downloads.speedPage.limit");
                    form.Save();
                }, Address(client), csrf, input, _failed?.Key == key ? FormError : null));
            }
            else
            {
                body.Add(new P(new Text("downloads.speedPage.unreachable")).Class("feature-card__note"));
            }

            var native = client.AcquisitionConnectionId is null;
            return new FeatureCard(new IconTile(Glyph(client.DownloadClientKindId == DownloadClientKind.Usenet ? "network" : "download", 18), UiTone.Info),
                native ? texts["downloads.client." + client.DownloadClientKindId] : client.Name,
                native ? null : texts["acquisition.connection.type." + client.DownloadClientKindId], null, body, []).Wide();
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
