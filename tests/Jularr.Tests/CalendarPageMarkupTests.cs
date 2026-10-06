using System.Text.RegularExpressions;
using Jularr.Web.Features.Localization;

namespace Jularr.Tests;

/// <summary>
/// The calendar and watchlist pages show each control once: one period navigation, one type
/// filter, one empty text, and no link to the page the user is already on.
/// </summary>
[TestClass]
public sealed class CalendarPageMarkupTests
{
    private static readonly string Root = FindRepositoryRoot();

    private static readonly string Calendar = Read("src", "Jularr.Web", "Pages", "Calendar", "Index.cshtml");

    [TestMethod]
    public void MonthViewHasOneNavigationAndOneTypeControl()
    {
        Assert.AreEqual(1, Count(Calendar, "Model.Link(date: Model.Previous)"), "One previous link.");
        Assert.AreEqual(1, Count(Calendar, "Model.Link(date: Model.Next)"), "One next link.");
        Assert.AreEqual(1, Count(Calendar, "@Model.Title"), "The period title is shown once.");
        Assert.AreEqual(1, Count(Calendar, "calendar.filter.typeAria"), "The type filter is the only type control.");
        Assert.AreEqual(1, Count(Calendar, "<aside class=\"calendar-sidebar-card\""), "The month side panel stays.");
        StringAssert.Contains(
            Calendar,
            "@if (Model.IsPeriodEmpty && Model.View != CalendarView.Month)",
            "In the month view the empty text is shown once, in the side panel.");

        foreach (var removed in new[] { "calendar-mini", "calendar-legend", "calendar.upcoming.open" })
        {
            Assert.IsFalse(Calendar.Contains(removed, StringComparison.Ordinal), $"{removed} is gone from the calendar page.");
        }
    }

    [TestMethod]
    public void ViewSwitchIsALabelledNavigation()
    {
        var nav = Regex.Match(Calendar, "<nav class=\"calendar-views\"[^>]*>").Value;
        StringAssert.Contains(nav, "aria-label=\"@ui[\"calendar.viewsAria\"]\"");
    }

    [TestMethod]
    public void CalendarStylesHaveNoLeftoversOfTheMiniCalendar()
    {
        var css = Read("src", "Jularr.Web", "wwwroot", "css", "calendar.css");
        Assert.IsFalse(css.Contains(".calendar-mini", StringComparison.Ordinal));
        Assert.IsFalse(css.Contains(".calendar-legend", StringComparison.Ordinal));
        Assert.IsFalse(css.Contains("border-inline-start", StringComparison.Ordinal), "Media types are not shown as edge stripes.");
    }

    [TestMethod]
    public void DiscoverCardsKeepOneFollowControlNextToThePrimaryAction()
    {
        var script = Read("src", "Jularr.Web", "wwwroot", "js", "discover.js");
        var card = Read("src", "Jularr.Web", "Pages", "Discover", "_DiscoverCard.cshtml");
        Assert.AreEqual(1, Count(card, "data-dc-follow "), "One follow control per preview.");
        Assert.AreEqual(1, Count(card, "data-dc-follow-franchise"), "Follow franchise sits next to it, once.");
        Assert.AreEqual(1, Count(script, "root.dataset.watchlistUrl,"), "The script posts one follow request.");
        Assert.IsFalse(script.Contains("localMediaId", StringComparison.Ordinal), "Library state is never posted from the browser.");
        Assert.IsFalse(script.Contains("detailsUrl", StringComparison.Ordinal), "Links are never posted from the browser.");
    }

    [TestMethod]
    public void WatchlistPagesHaveNoHelperTextOrClientSuppliedLinks()
    {
        var watchlist = Read("src", "Jularr.Web", "Pages", "Legacy", "Watchlist", "Index.cshtml");
        var franchise = Read("src", "Jularr.Web", "Pages", "Legacy", "Franchises", "Details.cshtml");
        foreach (var key in new[] { "watchlist.subtitle", "watchlist.franchiseHint", "watchlist.directFollow", "franchise.relationsHint" })
        {
            Assert.IsFalse(watchlist.Contains(key, StringComparison.Ordinal) || franchise.Contains(key, StringComparison.Ordinal), key);
            Assert.IsFalse(UiTranslationResources.All.Any(message => message.Key == key), $"{key} is removed from the catalog.");
        }

        Assert.IsFalse(watchlist.Contains("name=\"detailsUrl\"", StringComparison.Ordinal));
        Assert.IsFalse(watchlist.Contains("name=\"localMediaId\"", StringComparison.Ordinal));
        Assert.IsFalse(franchise.Contains("<option value=", StringComparison.Ordinal), "No hard-coded relation editor.");
    }

    [TestMethod]
    public void FranchisePagesBelongToTheWatchlistNavigation()
    {
        var entry = UiNavigationCatalog.App.Single(item => item.Id == "watchlist");
        CollectionAssert.Contains(entry.Matches, "/Franchises");
        CollectionAssert.Contains(entry.Matches, "/Watchlist");
    }

    private static int Count(string text, string value) =>
        Regex.Matches(text, Regex.Escape(value)).Count;

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root, .. parts]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
