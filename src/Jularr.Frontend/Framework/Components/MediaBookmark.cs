using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.User.Media.V1;

namespace Jularr.Frontend.Framework
{
    // Same watchlist form as Details.
    public sealed class MediaBookmark(FrontendAssets assets, long workId, bool? isWatchlisted = null, string? returnUrl = null, string? antiforgeryToken = null) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var action = "/Details/" + workId.ToString(CultureInfo.InvariantCulture);
            if (isWatchlisted is not { } watched)
            {
                new A(new Icon("bookmark", assets)).Class("button button--icon").Attribute("href", action)
                    .Attribute("aria-label", texts["media.watchlist.manage"]).WriteTo(writer, encoder, texts);
                return;
            }

            var button = new Button(new Icon("bookmark", assets)).IconOnly(texts[watched ? "media.watchlist.remove" : "media.watchlist.add"]);
            button.Attribute("aria-pressed", watched ? "true" : "false");
            if (returnUrl is not null) action += "?returnUrl=" + Uri.EscapeDataString(returnUrl);
            new Form<UpdateWatchlistDataV1>(form =>
            {
                form.Hidden(data => data.IsWatchlisted);
                form.Submit(button);
            }, action, antiforgeryToken, new UpdateWatchlistDataV1(!watched)).WriteTo(writer, encoder, texts);
        }
    }
}
