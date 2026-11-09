using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Playback;

namespace Jularr.Frontend.Framework
{
    // The three dots of a card in the continue rows (watching, listening, reading): take the title out of the row (its progress stays), mark it watched, read or heard
    // or not, add it to the favorites or take it out, open its details. Every entry is a form of the title's page (`/Details/{id}`, its Progress, Favorites and HideContinue
    // handlers), so the menu works without script; MediaCard.ts sends them in the background, drops the card of a row that no longer fits and flips the favorites entry.
    public sealed class ContinueMenu(FrontendAssets assets, long workId, long? unitId, bool isFavorite, MediaAssetType assetType, string? antiforgeryToken, string returnUrl) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var details = "/Details/" + workId.ToString(CultureInfo.InvariantCulture);
            var (watched, unwatched) = assetType switch
            {
                MediaAssetType.Ebook or MediaAssetType.Comic => ("progress.markRead", "progress.markUnread"),
                MediaAssetType.Audio => ("progress.markHeard", "progress.markUnheard"),
                _ => ("progress.markWatched", "progress.markUnwatched")
            };

            HtmlElement Item(string kind, string icon, string labelKey, string handler, params (string Name, string Value)[] fields)
            {
                var button = new Button(new Icon(icon, assets, 16), new Span(new Text(labelKey))).Submit().Class("action-menu__item");
                return new PostForm(details + handler, antiforgeryToken, [("returnUrl", returnUrl), .. fields], button).Attribute("data-card-menu", kind).Attribute("data-failed", texts["continue.failed"]).Attribute("hx-boost", "false");
            }

            IEnumerable<(string Name, string Value)> unit() => unitId is { } id ? [("unitId", id.ToString(CultureInfo.InvariantCulture))] : [];
            var favorite = Item("favorite", "heart", isFavorite ? "media.favorites.remove" : "media.favorites.add", "", ("isWatchlisted", isFavorite ? "false" : "true"));
            favorite.Attribute("data-add", texts["media.favorites.add"]).Attribute("data-remove", texts["media.favorites.remove"]).Attribute("data-state", isFavorite ? "true" : "false");
            new ActionMenu(new Icon("dots", assets, 18), [
                Item("remove", "close", "continue.remove", "?handler=HideContinue"),
                Item("watched", "check", watched, "?handler=Progress", [.. unit(), ("action", ((byte)ProgressAction.Watched).ToString(CultureInfo.InvariantCulture))]),
                Item("unwatched", "eye", unwatched, "?handler=Progress", [.. unit(), ("action", ((byte)ProgressAction.Unwatched).ToString(CultureInfo.InvariantCulture))]),
                favorite,
                new Divider().Class("action-menu__divider"),
                new A(new Icon("info", assets, 16), new Span(new Text("media.openDetails"))).Class("action-menu__item").Attribute("href", details)
            ], "button button--icon media-card__menu-button", "media.cardMenu").WriteTo(writer, encoder, texts);
        }

        private sealed class Divider() : HtmlElement("hr");
    }
}
