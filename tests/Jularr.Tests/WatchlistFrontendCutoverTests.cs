using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Watchlist;
using Jularr.Web.Pages.Watchlist;
using Jularr.Web.Ui.Pages;

namespace Jularr.Tests;

[TestClass]
public sealed class WatchlistFrontendCutoverTests
{
    [TestMethod]
    public void WatchlistTable_UsesSameTypedItemsForCardsAndRows()
    {
        var item = new WatchlistItem(
            new WatchlistIdentity(WatchlistMediaType.Anime, "anilist", "123"),
            "Example title",
            null,
            "https://example.com/cover.jpg",
            null,
            null,
            2026,
            null,
            "https://anilist.co/anime/123",
            null,
            null,
            true);

        var table = new WatchlistTable([item], UiTextBundle.English, UiTableMode.Cards);
        var cards = (UiTableView)table.ViewModel;

        Assert.AreEqual("_UiDataList", table.PartialName);
        Assert.AreEqual(UiTableMode.Cards, cards.Mode);
        Assert.AreEqual(1, cards.Cards.Count);
        Assert.AreEqual("Example title", cards.Cards[0].Title);
        Assert.AreEqual(item.CoverImageUrl, cards.Cards[0].CoverUrl);
        Assert.AreSame(item, cards.Cards[0].Item);
        Assert.AreEqual($"target-{item.StableId:D}", cards.Cards[0].Anchor);
        Assert.AreSame(item, cards.Rows[0].Item);

        var rows = (UiTableView)new WatchlistTable([item], UiTextBundle.English, UiTableMode.Rows).ViewModel;
        Assert.AreEqual(UiTableMode.Rows, rows.Mode);
        Assert.AreEqual(cards.Rows[0].Values[0], rows.Rows[0].Values[0]);
        Assert.AreEqual(cards.Cards[0].Anchor, rows.Rows[0].Anchor);
    }

    [TestMethod]
    public void WatchlistTable_MissingCoverStaysMissing()
    {
        var item = new WatchlistItem(
            new WatchlistIdentity(WatchlistMediaType.Book, "catalog", "456"),
            "Coverless book",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            true);

        var view = (UiTableView)new WatchlistTable([item], UiTextBundle.English, UiTableMode.Cards).ViewModel;

        Assert.IsNull(view.Cards[0].CoverUrl);
        Assert.IsNotNull(view.DetailsPartial);
    }
}
