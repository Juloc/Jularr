using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Localization;
using Jularr.Web.Ui;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Web.Features.Discovery;

/// <summary>
/// The data attributes that wire <c>discover.js</c> to the server: the card action handlers of the Discover page, the texts the script
/// shows and the request status labels. Discover and Home both host the feed, so the contract lives in one place instead of being copied
/// into each page. <paramref name="bodyUrl"/> names the page that serves the Body handler when the host is not Discover itself.
/// </summary>
public static class DiscoverFeedMarkup
{
    public static IHtmlContent Attributes(IUrlHelper url, UiTextBundle ui, bool isOwner, string? bodyUrl = null)
    {
        var builder = new HtmlContentBuilder();

        void Add(string name, string? value)
        {
            builder.AppendHtml($" {name}=\"");
            builder.Append(value ?? "");
            builder.AppendHtml("\"");
        }

        Add("data-is-owner", isOwner.ToString().ToLowerInvariant());
        Add("data-resolve-url", url.Page("/Discover/Index", "Resolve"));
        Add("data-open-url", url.Page("/Discover/Index", "Open"));
        Add("data-request-url", url.Page("/Discover/Index", "Request"));
        Add("data-request-status-url", url.Page("/Discover/Index", "RequestStatus"));
        Add("data-watchlist-url", url.Page("/Discover/Index", "Watchlist"));
        Add("data-franchise-url", url.Page("/Discover/Index", "FollowFranchise"));
        Add("data-text-action-failed", ui["discover.card.actionFailed"]);
        Add("data-text-follow", ui["discover.card.follow"]);
        Add("data-text-followed", ui["discover.card.followed"]);
        Add("data-text-unfollow", ui["discover.card.unfollow"]);
        Add("data-text-franchise-followed", ui["discover.card.franchiseFollowed"]);
        Add("data-text-open", ui["requests.open"]);
        Add("data-text-source-for", ui["discover.import.sourceFor"]);
        Add("data-text-source-url", ui["discover.import.sourceUrlHeading"]);
        foreach (var status in new[] { "pending", "approved", "searching", "downloading", "importing", "completed", "failed", "rejected" })
        {
            Add($"data-status-{status}", ui[ConsumerAcquisitionLabels.StatusKey(status)]);
        }

        if (bodyUrl is not null)
        {
            Add("data-body-url", bodyUrl);
        }

        return builder;
    }
}
