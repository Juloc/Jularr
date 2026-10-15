using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.User.Media.V1;

namespace Jularr.Frontend.Framework
{
    // Same watchlist form as Details.
    public sealed class MediaBookmark(FrontendAssets assets, long workId, bool isWatchlisted, string returnUrl, string? antiforgeryToken) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var button = new Button(new Icon("bookmark", assets)).IconOnly(texts[isWatchlisted ? "media.watchlist.remove" : "media.watchlist.add"]);
            button.Attribute("aria-pressed", isWatchlisted ? "true" : "false");
            var action = "/Details/" + workId.ToString(CultureInfo.InvariantCulture) + "?returnUrl=" + Uri.EscapeDataString(returnUrl);
            new Form<UpdateWatchlistDataV1>(form =>
            {
                form.Hidden(data => data.IsWatchlisted);
                form.Submit(button);
            }, action, antiforgeryToken, new UpdateWatchlistDataV1(!isWatchlisted)).WriteTo(writer, encoder, texts);
        }
    }
}
