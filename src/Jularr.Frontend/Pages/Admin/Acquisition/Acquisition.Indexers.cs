using System.Globalization;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Acquisition
{
    // Indexers and subtitle providers as one compact list (the owner's variant B, as Prowlarr): name with its kind, what it serves, state, today's use and priority;
    // every setting, the test, the test search and deleting live in the edit dialog, which the address opens.
    public sealed partial class AcquisitionModel
    {
        [BindProperty(SupportsGet = true, Name = "edit")]
        public bool ShowEdit { get; set; }

        // The admin pages of indexers and subtitle providers are lists; the setup and the download clients keep their cards.
        private bool IsList => !SetupWizard.IsActive(Request) && Role is AcquisitionConnectionRole.Search or AcquisitionConnectionRole.Subtitles;

        private static bool HasLimits(AcquisitionConnectionType type) => type is AcquisitionConnectionType.Newznab or AcquisitionConnectionType.Nyaa;

        private IUiComponent ConnectionList(IReadOnlyList<AcquisitionConnectionDetailsV1> items, string? csrf)
        {
            if (items.Count == 0)
            {
                return new Div(new Div(new IconTile(Glyph(Role == AcquisitionConnectionRole.Subtitles ? "subtitles" : "search", 22), UiTone.Neutral),
                    new P(new Text("acquisition.connection.none." + Scope)).Attribute("role", "status")).Class("list-empty")).Class("list");
            }

            // The subtitle providers have no daily limits, so their list has no column for today.
            var indexers = Role == AcquisitionConnectionRole.Search;
            var columns = new List<ListColumn>
            {
                new(indexers ? "acquisition.column.indexer" : "acquisition.column.provider", ListRole.Main),
                new("acquisition.column.for", ListRole.Data, 1),
                new("downloads.state", ListRole.Status)
            };
            if (indexers) columns.Add(new ListColumn("acquisition.column.today", ListRole.Data));
            columns.AddRange([new ListColumn("acquisition.connection.priority", ListRole.Data, 2), new ListColumn("common.actions", ListRole.Actions)]);

            var rows = items.Select(item =>
            {
                var state = State(item);
                var note = state switch
                {
                    "backoff" => new Span(new Text("acquisition.connection.backoff"), new ValueText(" "), new LocalTime(item.RetryAt!.Value)),
                    "error" => new Span(new Truncated(texts["errors." + item.ErrorKey])),
                    _ => null
                };
                var identity = new ListIdentity(new IconTile(Glyph(Role == AcquisitionConnectionRole.Subtitles ? "subtitles" : "search", 18), UiTone.Info),
                    new A(new Truncated(item.Name)).Class("list-identity__title").Attribute("href", EditAddress(item.Id)),
                    new Span(new Tag(new Text(KindKey(item.AcquisitionConnectionTypeId))), new ValueText(Host(item.BaseUrl))).Class("connections-list__kind"), note);

                var actions = new Div(
                    Post("Test", item.Id, csrf, [], new Button(Glyph("pulse", 16)).IconOnly(texts["acquisition.connection.test"]).Submit()),
                    new A(Glyph("pencil", 16)).Class("button button--icon").Attribute("href", EditAddress(item.Id)).TextAttribute("aria-label", "common.edit")).Class("list-actions");

                var cells = new List<IUiComponent> { identity, new Span(new ValueText(Serves(item))).Class("list-value"), StateChipOf(item, state) };
                var caption = new List<IUiComponent> { ListPanel.Fact("acquisition.column.for", new ValueText(Serves(item))) };
                if (indexers)
                {
                    cells.Add(Today(item));
                    if (HasLimits(item.AcquisitionConnectionTypeId)) caption.Add(ListPanel.Fact("acquisition.column.today", TodayText(item)));
                }

                cells.AddRange([new Span(new ValueText(item.Priority.ToString(CultureInfo.CurrentCulture))).Class("list-value"), actions]);
                caption.Add(ListPanel.Fact("acquisition.connection.priority", new ValueText(item.Priority.ToString(CultureInfo.CurrentCulture))));
                return new ListRow(cells, caption);
            }).ToList();

            return new ListPanel("acquisition.role." + Role, columns, rows).Modifier("connections-list connections-list--" + Role.ToString().ToLowerInvariant());
        }

        // Today's queries against the daily limit as a bar, the grabs beside the count; without a limit the bar's place says so.
        private IUiComponent Today(AcquisitionConnectionDetailsV1 item)
        {
            if (!HasLimits(item.AcquisitionConnectionTypeId)) return Dim();
            IUiComponent gauge = item.QueryLimit is > 0 and var limit
                ? new Progress().Attribute("max", limit.ToString(CultureInfo.InvariantCulture)).Attribute("value", Math.Min(item.QueryCount, limit).ToString(CultureInfo.InvariantCulture))
                    .TextAttribute("aria-label", "acquisition.connection.usage").Class(item.QueryCount >= limit ? "connection-usage__bar connection-usage__bar--full" : "connection-usage__bar")
                : new Span(new Text("acquisition.connection.noLimit")).Class("connection-usage__none");
            return new Div(gauge, new Span(TodayText(item)).Class("connection-usage__text")).Class("connection-usage");
        }

        private static IUiComponent TodayText(AcquisitionConnectionDetailsV1 item) =>
            new Span(new ValueText(Count(item.QueryCount, item.QueryLimit) + " "), new Text("acquisition.connection.queries"),
                new ValueText(" · " + Count(item.GrabCount, item.GrabLimit) + " "), new Text("acquisition.connection.grabs"));

        // What a connection serves: an indexer its chosen media types (none: all), a book source its kind of book, a subtitle provider films and series.
        private string Serves(AcquisitionConnectionDetailsV1 item) => item.AcquisitionConnectionTypeId switch
        {
            AcquisitionConnectionType.Nyaa or AcquisitionConnectionType.Narou => texts[MediaTypeLabel.Key(MediaType.Book, MediaSubtype.LightNovel)],
            AcquisitionConnectionType.Gutenberg or AcquisitionConnectionType.Opds or AcquisitionConnectionType.AnnasArchive => texts[MediaTypeLabel.Key(MediaType.Book, null)],
            AcquisitionConnectionType.OpenSubtitles or AcquisitionConnectionType.SubDl => texts[MediaTypeLabel.Key(MediaType.Movie, null)] + ", " + texts[MediaTypeLabel.Key(MediaType.Series, null)],
            _ when item.MediaTypeIds.Length == 0 => texts["acquisition.connection.allTypes"],
            _ => string.Join(", ", item.MediaTypeIds.Select(id => texts[MediaTypeLabel.Key((MediaType)id, null)]))
        };

        // The kind a list row names beside the address: Usenet, Torrent or Books for an indexer, the service for a subtitle provider.
        private static string KindKey(AcquisitionConnectionType type) => type switch
        {
            AcquisitionConnectionType.Newznab => "acquisition.source.Usenet",
            AcquisitionConnectionType.Nyaa => "acquisition.source.Torrent",
            AcquisitionConnectionType.Gutenberg or AcquisitionConnectionType.Opds or AcquisitionConnectionType.AnnasArchive or AcquisitionConnectionType.Narou => "acquisition.source.Books",
            _ => "acquisition.connection.type." + type
        };

        private static string Host(string? address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : "";

        // An existing connection's dialog: what is known about it and its tools above, its settings below with Cancel and Save.
        private IUiComponent EditDialog(AcquisitionConnectionDetailsV1 item, string? csrf)
        {
            var state = State(item);
            var tools = new List<IUiComponent>
            {
                Post("Test", item.Id, csrf, [("edit", "true")], new Button(Glyph("pulse", 14), new Text("acquisition.connection.test")).Compact().Soft().Submit())
            };
            if (HasLimits(item.AcquisitionConnectionTypeId))
            {
                tools.Add(new A(Glyph("search", 14), new Text("acquisition.connection.search")).Class("button button--compact button--soft").Attribute("href", Address(item.Id) + "&search=true"));
            }

            tools.Add(new A(Glyph("trash", 14), new Text("acquisition.connection.delete")).Class("button button--compact button--warn").Attribute("href", Address(item.Id) + "&delete=true"));
            var facts = new List<IUiComponent> { StateChipOf(item, state), new Span(new Text("acquisition.connection.lastTest"), new ValueText(" "), Time(item.CheckedAt)).Class("connection-editor__fact") };
            if (state == "error") facts.Add(new Span(new ValueText(texts["errors." + item.ErrorKey])).Class("connection-editor__error"));
            if (HasLimits(item.AcquisitionConnectionTypeId)) facts.Add(new Span(new Text("acquisition.connection.today"), new ValueText(" "), TodayText(item)).Class("connection-editor__fact"));

            return new Modal(assets, "connection-editor", "acquisition.connection.type." + item.AcquisitionConnectionTypeId, [
                new Div(new Div([.. facts]).Class("connection-editor__facts"), new Div([.. tools]).Class("connection-editor__tools")).Class("connection-editor__head"),
                ConnectionForm(item.AcquisitionConnectionTypeId, csrf, item, inDialog: true)
            ], open: true, returnTo: Address());
        }

        private string EditAddress(long id) => Address(id) + "&edit=true";
    }
}
